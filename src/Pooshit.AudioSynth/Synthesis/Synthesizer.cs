using System;
using Pooshit.AudioSynth.Audio;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Pull-based voice engine that implements <see cref="ISynthesizer"/>; turns MIDI-style note events
    /// into voices, renders them in fixed-size internal blocks, mixes each voice into real stereo
    /// placement via per-voice equal-power L/R gains (combining the channel's dynamic pan with the
    /// voice's static SF2 pan; non-stereo output collapses to the legacy centre <c>panGain</c>), then an
    /// optional master <see cref="Reverb"/> insert (present only when <see cref="SynthesizerOptions.Reverb"/>
    /// is configured and output is stereo; absent, it leaves the master path bit-for-bit unchanged), a
    /// master soft-clip stage, then a single NaN/Inf-safe finalize choke point (INV-2). Holds a per-channel
    /// pitch-bend factor that fans out to the channel's sounding voices and is inherited by notes started
    /// while a bend is active; a centered channel (1.0) leaves every voice's increment bit-for-bit
    /// unchanged (INV-3). All buffers, including the reverb's delay lines, are ctor-sized; steady-state
    /// <see cref="Read"/> allocates nothing.
    /// </summary>
    public sealed class Synthesizer : ISynthesizer {

        const int ChannelCount = 16;

        /// <summary>
        /// Master-bus soft-clip knee: normal-level material at or below this magnitude passes through
        /// unchanged; above it, <see cref="ApplyMasterBus"/> compresses toward the ±1 ceiling (DiVoid #7126 §5.3).
        /// </summary>
        const float MasterBusKneeThreshold = 0.9f;

        /// <summary>Output channel count for which per-voice equal-power stereo placement applies.</summary>
        const int StereoChannelCount = 2;

        /// <summary>
        /// Quarter-turn used by <see cref="EqualPowerGains"/> to map pan ∈ [-1,1] onto the quarter-circle
        /// of constant-power L/R gains.
        /// </summary>
        const double PanQuarterTurn = Math.PI / 4.0;

        readonly SynthesizerOptions options;
        readonly IPatch[] channelPatch;
        readonly GainRamp[] channelGain;
        readonly float[] channelGainBlock;
        readonly float[] channelBendFactor;
        readonly float[] channelPan;
        readonly VoiceSlot[] pool;
        readonly float[] scratch;
        readonly float[] master;
        readonly float panGain;
        readonly Reverb? reverb;

        /// <summary>
        /// Creates a <see cref="Synthesizer"/> with the given options; <paramref name="defaultPatch"/> fills
        /// all 16 channels until overridden, and every channel's mix gain defaults to unity.
        /// </summary>
        /// <param name="options">immutable engine configuration</param>
        /// <param name="defaultPatch">initial patch for every channel</param>
        public Synthesizer(SynthesizerOptions options, IPatch defaultPatch) {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (defaultPatch is null) throw new ArgumentNullException(nameof(defaultPatch));
            Format = new AudioFormat(options.SampleRate, options.Channels);
            channelPatch = new IPatch[ChannelCount];
            for (int i = 0; i < channelPatch.Length; i++)
                channelPatch[i] = defaultPatch;
            channelGain = new GainRamp[ChannelCount];
            for (int i = 0; i < channelGain.Length; i++) {
                channelGain[i] = new GainRamp(options.SampleRate);
                channelGain[i].SetTarget(1f);
            }
            channelGainBlock = new float[ChannelCount * options.BlockFrames];
            channelBendFactor = new float[ChannelCount];
            for (int i = 0; i < channelBendFactor.Length; i++)
                channelBendFactor[i] = 1f;
            channelPan = new float[ChannelCount];
            pool = new VoiceSlot[options.MaxVoices];
            scratch = new float[options.BlockFrames];
            master = new float[options.BlockFrames * options.Channels];
            panGain = (float)(1.0 / Math.Sqrt(options.Channels));
            reverb = options.Reverb != null && options.Channels == StereoChannelCount
                ? new Reverb(options.Reverb, options.SampleRate)
                : null;
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <inheritdoc/>
        public void SetChannelPatch(int channel, IPatch patch) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            if (patch is null) throw new ArgumentNullException(nameof(patch));
            channelPatch[channel] = patch;
        }

        /// <inheritdoc/>
        public void SetChannelGain(int channel, float gain) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            channelGain[channel].SetTarget(gain);
        }

        /// <inheritdoc/>
        public void SetChannelPitchBend(int channel, float semitones) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            float factor = (float)Math.Pow(2.0, semitones / 12.0);
            channelBendFactor[channel] = factor;
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel)
                    slot.Voice!.SetPitchBend(factor);
            }
        }

        /// <inheritdoc/>
        public void SetChannelPan(int channel, float pan) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            channelPan[channel] = pan;
        }

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            int freeSlot = FindFreeSlot();
            if (freeSlot < 0)
                return;

            IVoice voice = channelPatch[channel].StartVoice(key, velocity);
            voice.SetPitchBend(channelBendFactor[channel]);
            ref VoiceSlot slot = ref pool[freeSlot];
            slot.IsOccupied = true;
            slot.Channel = channel;
            slot.Key = key;
            slot.Voice = voice;
        }

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) {
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel && slot.Key == key)
                    slot.Voice!.Release();
            }
        }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            if (destination.Length % options.Channels != 0)
                throw new ArgumentException(
                    $"destination length ({destination.Length}) must be a multiple of the channel count ({options.Channels}).",
                    nameof(destination));

            int channels = options.Channels;
            bool isStereo = channels == StereoChannelCount;
            int blockFrames = options.BlockFrames;
            int totalSamples = destination.Length;
            int written = 0;

            while (written < totalSamples) {
                int remainingSamples = totalSamples - written;
                int blockSamples = remainingSamples < blockFrames * channels
                    ? remainingSamples
                    : blockFrames * channels;
                int frames = blockSamples / channels;

                Span<float> masterSlice = master.AsSpan(0, frames * channels);
                masterSlice.Clear();

                Span<float> scratchSlice = scratch.AsSpan(0, frames);

                PrecomputeChannelGainBlock(frames);

                for (int v = 0; v < pool.Length; v++) {
                    ref VoiceSlot slot = ref pool[v];
                    if (!slot.IsOccupied)
                        continue;

                    scratchSlice.Clear();
                    slot.Voice!.RenderBlock(scratchSlice);

                    int channelBase = slot.Channel * options.BlockFrames;

                    if (isStereo) {
                        float combinedPan = Clamp(channelPan[slot.Channel] + slot.Voice.Pan, -1f, 1f);
                        EqualPowerGains(combinedPan, out float leftGain, out float rightGain);

                        for (int frame = 0; frame < frames; frame++) {
                            float pre = scratchSlice[frame] * channelGainBlock[channelBase + frame];
                            int baseIndex = frame * channels;
                            masterSlice[baseIndex] += pre * leftGain;
                            masterSlice[baseIndex + 1] += pre * rightGain;
                        }
                    } else {
                        for (int frame = 0; frame < frames; frame++) {
                            float mixed = scratchSlice[frame] * channelGainBlock[channelBase + frame] * panGain;
                            int baseIndex = frame * channels;
                            for (int ch = 0; ch < channels; ch++)
                                masterSlice[baseIndex + ch] += mixed;
                        }
                    }

                    if (!slot.Voice.IsActive) {
                        slot.IsOccupied = false;
                        slot.Voice = null;
                    }
                }

                reverb?.Process(masterSlice);

                ApplyMasterBus(masterSlice);
                Finalize(masterSlice);

                masterSlice.CopyTo(destination.Slice(written));
                written += masterSlice.Length;
            }

            return totalSamples;
        }

        /// <summary>
        /// Advances every channel's <see cref="GainRamp"/> once per frame of the block into
        /// <see cref="channelGainBlock"/>, before any voice is mixed (INV-1, DiVoid #7126 §5.2).
        /// </summary>
        /// <param name="frames">the number of frames in the current block</param>
        void PrecomputeChannelGainBlock(int frames) {
            for (int ch = 0; ch < ChannelCount; ch++) {
                int channelBase = ch * options.BlockFrames;
                for (int frame = 0; frame < frames; frame++)
                    channelGainBlock[channelBase + frame] = channelGain[ch].AdvanceFrame();
            }
        }

        int FindFreeSlot() {
            for (int i = 0; i < pool.Length; i++) {
                if (!pool[i].IsOccupied)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Maps a combined pan position ∈ [-1,1] onto constant-power left/right gains via a quarter-circle
        /// rotation (<c>θ = (pan+1)·π/4</c>, <c>left = cos θ</c>, <c>right = sin θ</c>), so
        /// <c>left² + right² = 1</c> at every position. Private: the engine is this law's sole consumer.
        /// </summary>
        /// <param name="pan">combined channel+voice pan, already clamped to [-1,1]</param>
        /// <param name="left">the resulting left-channel gain</param>
        /// <param name="right">the resulting right-channel gain</param>
        static void EqualPowerGains(float pan, out float left, out float right) {
            double theta = (pan + 1.0) * PanQuarterTurn;
            left = (float)Math.Cos(theta);
            right = (float)Math.Sin(theta);
        }

        static float Clamp(float value, float min, float max) {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        /// <summary>
        /// Stateless per-sample soft-clip on the master bus, before <see cref="Finalize"/> (INV-2): unity
        /// below the knee, <c>tanh</c>-saturating toward ±1 above it; NaN/Inf samples pass through untouched.
        /// </summary>
        static void ApplyMasterBus(Span<float> block) {
            for (int i = 0; i < block.Length; i++) {
                float x = block[i];
                if (float.IsNaN(x) || float.IsInfinity(x))
                    continue;

                float magnitude = Math.Abs(x);
                if (magnitude <= MasterBusKneeThreshold)
                    continue;

                float sign = x < 0f ? -1f : 1f;
                float excess = (magnitude - MasterBusKneeThreshold) / (1f - MasterBusKneeThreshold);
                block[i] = sign * (MasterBusKneeThreshold + (1f - MasterBusKneeThreshold) * (float)Math.Tanh(excess));
            }
        }

        static void Finalize(Span<float> block) {
            for (int i = 0; i < block.Length; i++) {
                float x = block[i];
                if (float.IsNaN(x) || float.IsInfinity(x)) {
                    block[i] = 0f;
                } else if (x > 1f) {
                    block[i] = 1f;
                } else if (x < -1f) {
                    block[i] = -1f;
                }
            }
        }
    }
}

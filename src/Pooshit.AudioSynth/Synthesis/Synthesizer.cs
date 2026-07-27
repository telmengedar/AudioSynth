using System;
using Pooshit.AudioSynth.Audio;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Pull-based voice engine that implements <see cref="ISynthesizer"/>; turns MIDI-style note events
    /// into voices, renders them in fixed-size internal blocks, mixes with equal-power centre pan and a
    /// zipper-free per-channel mix gain through a master soft-clip stage, then a single NaN/Inf-safe
    /// finalize choke point (INV-2). All buffers are ctor-sized; steady-state <see cref="Read"/> allocates nothing.
    /// </summary>
    public sealed class Synthesizer : ISynthesizer {

        const int ChannelCount = 16;

        /// <summary>
        /// Master-bus soft-clip knee: normal-level material at or below this magnitude passes through
        /// unchanged; above it, <see cref="ApplyMasterBus"/> compresses toward the ±1 ceiling (DiVoid #7126 §5.3).
        /// </summary>
        const float MasterBusKneeThreshold = 0.9f;

        readonly SynthesizerOptions options;
        readonly IPatch[] channelPatch;
        readonly GainRamp[] channelGain;
        readonly float[] channelGainBlock;
        readonly VoiceSlot[] pool;
        readonly float[] scratch;
        readonly float[] master;
        readonly float panGain;

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
            pool = new VoiceSlot[options.MaxVoices];
            scratch = new float[options.BlockFrames];
            master = new float[options.BlockFrames * options.Channels];
            panGain = (float)(1.0 / Math.Sqrt(options.Channels));
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
        public void NoteOn(int channel, int key, int velocity) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            int freeSlot = FindFreeSlot();
            if (freeSlot < 0)
                return;

            IVoice voice = channelPatch[channel].StartVoice(key, velocity);
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
                    for (int frame = 0; frame < frames; frame++) {
                        float mixed = scratchSlice[frame] * channelGainBlock[channelBase + frame] * panGain;
                        int baseIndex = frame * channels;
                        for (int ch = 0; ch < channels; ch++)
                            masterSlice[baseIndex + ch] += mixed;
                    }

                    if (!slot.Voice.IsActive) {
                        slot.IsOccupied = false;
                        slot.Voice = null;
                    }
                }

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

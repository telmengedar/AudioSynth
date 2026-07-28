using System;
using Pooshit.AudioSynth.Audio;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Pull-based voice engine that implements <see cref="ISynthesizer"/>; turns MIDI-style note events
    /// into voices, renders them in fixed-size internal blocks, mixes each voice into real stereo
    /// placement via per-voice equal-power L/R gains (combining the channel's dynamic pan with the
    /// voice's static SF2 pan; non-stereo output collapses to the legacy centre <c>panGain</c>), then an
    /// optional <see cref="Reverb"/> (present only when <see cref="SynthesizerOptions.Reverb"/> is
    /// configured and output is stereo; absent, it leaves the master path bit-for-bit unchanged), a
    /// master soft-clip stage, then a single NaN/Inf-safe finalize choke point (INV-2). The reverb is
    /// routed as a send-return: by default (<see cref="SynthesizerOptions.GlobalReverb"/> = <c>false</c>)
    /// each voice also adds into a per-channel-weighted stereo send bus (<c>clamp01(channelReverbSend[ch] +
    /// voice.ReverbSend)</c>, honouring CC91 and SF2 gen-16 additively), and the reverb reads that bus; when
    /// <see cref="SynthesizerOptions.GlobalReverb"/> is <c>true</c> the reverb reads the master directly
    /// (every voice sends fully), reproducing the pre-send-bus uniform master-insert bit-for-bit. An
    /// optional <see cref="Chorus"/> is wired the same way through its own send bus
    /// (<see cref="SynthesizerOptions.Chorus"/>/<see cref="SynthesizerOptions.GlobalChorus"/>, CC93 + SF2
    /// gen-15, additive) and runs its stage before the reverb stage; both effects implement
    /// <see cref="IAudioEffect"/> but are invoked explicitly, not through a generic pipeline. Holds a
    /// per-channel pitch-bend factor that fans out to the channel's sounding voices and is inherited by
    /// notes started while a bend is active; a centered channel (1.0) leaves every voice's increment
    /// bit-for-bit unchanged (INV-3). Also holds a per-channel mod-wheel (CC1) vibrato amount that fans
    /// out to sounding voices and is inherited by future notes on the channel; a channel that never
    /// raises the wheel (amount 0) leaves every voice's increment bit-for-bit unchanged. Also holds a
    /// per-channel sustain-pedal state (CC64): while held, a
    /// <see cref="NoteOff"/> on the channel marks the matching <see cref="VoiceSlot.PendingRelease"/>
    /// instead of releasing the voice, and disengaging the pedal sweeps the pool releasing every deferred
    /// voice on that channel; a channel that never sustains takes the unchanged immediate-release path
    /// bit-for-bit. When the voice pool is full, <see cref="NoteOn"/> no longer drops the note: it steals
    /// the best-candidate slot instead (released voices first, then the quietest sounding voice, then the
    /// oldest), fast-fading that voice to silence while it keeps rendering through this same unchanged mix
    /// loop, and only starting the new note in-place once the fade reaches silence — a song that never
    /// exceeds the pool never takes this path and renders bit-for-bit as before. All buffers, including
    /// both effects' delay lines and send buses, are ctor-sized; steady-state <see cref="Read"/> allocates
    /// nothing.
    /// </summary>
    public sealed class Synthesizer : ISynthesizer {

        const int ChannelCount = 16;

        /// <summary>
        /// Master-bus soft-clip knee: normal-level material at or below this magnitude passes through
        /// unchanged; above it, <see cref="ApplyMasterBus"/> compresses toward the ±1 ceiling (DiVoid #7126 §5.3).
        /// </summary>
        const float MasterBusKneeThreshold = 0.9f;

        /// <summary>
        /// Master headroom attenuation applied to every finite sample before the soft-clip in
        /// <see cref="ApplyMasterBus"/>, so normal-playing peaks fall below <see cref="MasterBusKneeThreshold"/>
        /// and the limiter no longer engages continuously on dense material (DiVoid BUG #7212, design #7213).
        /// </summary>
        const float MasterHeadroomTrim = 0.5f;

        /// <summary>Output channel count for which per-voice equal-power stereo placement applies.</summary>
        const int StereoChannelCount = 2;

        /// <summary>
        /// <see cref="VoiceSlot.PendingChannel"/> sentinel meaning "no note is deferred behind this slot's
        /// declick fade-out".
        /// </summary>
        const int NoPendingNote = -1;

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
        readonly float[] channelModWheel;
        readonly float[] channelPan;
        readonly float[] channelReverbSend;
        readonly float[] channelChorusSend;
        readonly bool[] channelSustain;
        readonly VoiceSlot[] pool;
        readonly float[] scratch;
        readonly float[] master;
        readonly float[] sendBus;
        readonly float[] chorusSendBus;
        readonly float panGain;
        readonly Reverb? reverb;
        readonly Chorus? chorus;
        readonly bool perChannelReverb;
        readonly bool perChannelChorus;
        int nextAge;

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
            channelModWheel = new float[ChannelCount];
            channelPan = new float[ChannelCount];
            channelReverbSend = new float[ChannelCount];
            channelChorusSend = new float[ChannelCount];
            channelSustain = new bool[ChannelCount];
            pool = new VoiceSlot[options.MaxVoices];
            scratch = new float[options.BlockFrames];
            master = new float[options.BlockFrames * options.Channels];
            panGain = (float)(1.0 / Math.Sqrt(options.Channels));
            reverb = options.Reverb != null && options.Channels == StereoChannelCount
                ? new Reverb(options.Reverb, options.SampleRate)
                : null;
            perChannelReverb = reverb != null && !options.GlobalReverb;
            sendBus = perChannelReverb ? new float[options.BlockFrames * options.Channels] : Array.Empty<float>();
            chorus = options.Chorus != null && options.Channels == StereoChannelCount
                ? new Chorus(options.Chorus, options.SampleRate)
                : null;
            perChannelChorus = chorus != null && !options.GlobalChorus;
            chorusSendBus = perChannelChorus ? new float[options.BlockFrames * options.Channels] : Array.Empty<float>();
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
        public void SetChannelModulation(int channel, float amount) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            channelModWheel[channel] = amount;
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel)
                    slot.Voice!.SetModWheel(amount);
            }
        }

        /// <inheritdoc/>
        public void SetChannelPan(int channel, float pan) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            channelPan[channel] = pan;
        }

        /// <inheritdoc/>
        public void SetChannelReverbSend(int channel, float level) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            channelReverbSend[channel] = level;
        }

        /// <inheritdoc/>
        public void SetChannelChorusSend(int channel, float level) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            channelChorusSend[channel] = level;
        }

        /// <inheritdoc/>
        public void SetChannelSustain(int channel, bool held) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            channelSustain[channel] = held;
            if (held)
                return;

            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel && slot.PendingRelease) {
                    slot.Voice!.Release();
                    slot.PendingRelease = false;
                    slot.Released = true;
                }
            }
        }

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            int freeSlot = FindFreeSlot();
            if (freeSlot >= 0) {
                StartVoiceInSlot(freeSlot, channel, key, velocity);
                return;
            }

            int victim = FindStealVictim();
            if (victim < 0)
                return;

            ref VoiceSlot slot = ref pool[victim];
            slot.Voice!.FastFadeForSteal();
            slot.PendingChannel = channel;
            slot.PendingKey = key;
            slot.PendingVelocity = velocity;
        }

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) {
            bool sustained = channelSustain[channel];
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel && slot.Key == key) {
                    if (sustained) {
                        slot.PendingRelease = true;
                    } else {
                        slot.Voice!.Release();
                        slot.Released = true;
                    }
                }
            }
        }

        /// <summary>
        /// Starts a new voice for <paramref name="channel"/>/<paramref name="key"/>/<paramref name="velocity"/>
        /// in <paramref name="slotIndex"/>, applying the channel's live pitch-bend and mod-wheel exactly as
        /// a fresh <see cref="NoteOn"/> does, and stamping a fresh age. Shared by the free-slot path and the
        /// deferred pending-note start (<see cref="Read"/>) so both allocate a voice identically.
        /// </summary>
        void StartVoiceInSlot(int slotIndex, int channel, int key, int velocity) {
            IVoice voice = channelPatch[channel].StartVoice(key, velocity);
            voice.SetPitchBend(channelBendFactor[channel]);
            voice.SetModWheel(channelModWheel[channel]);
            ref VoiceSlot slot = ref pool[slotIndex];
            slot.IsOccupied = true;
            slot.Channel = channel;
            slot.Key = key;
            slot.Voice = voice;
            slot.PendingRelease = false;
            slot.Released = false;
            slot.Age = nextAge++;
            slot.PendingChannel = NoPendingNote;
        }

        /// <summary>
        /// Scans the occupied, non-draining slots for the best voice-stealing victim: the smallest
        /// lexicographic tuple <c>(releasedTier, currentGain, age)</c> — a released voice (immediate
        /// release or a sustain-deferred release) dies before any still-held voice; among voices tied on
        /// that tier the quietest dies first; age breaks remaining ties toward the oldest. Slots already
        /// holding a pending note are excluded — they are already committed to a steal and must not lose
        /// it. Returns -1 when every occupied slot is already draining (pathological: more note-ons than
        /// the pool has slots inside one render).
        /// </summary>
        int FindStealVictim() {
            int best = -1;
            int bestReleasedTier = 0;
            float bestGain = 0f;
            int bestAge = 0;

            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (!slot.IsOccupied || slot.PendingChannel != NoPendingNote)
                    continue;

                int releasedTier = slot.Released || slot.PendingRelease ? 0 : 1;
                float gain = slot.Voice!.CurrentGain;
                int age = slot.Age;

                if (best < 0 || IsBetterVictim(releasedTier, gain, age, bestReleasedTier, bestGain, bestAge)) {
                    best = i;
                    bestReleasedTier = releasedTier;
                    bestGain = gain;
                    bestAge = age;
                }
            }

            return best;
        }

        /// <summary>
        /// Lexicographic comparison for <see cref="FindStealVictim"/>: smaller <paramref name="releasedTier"/>
        /// wins outright; a tie falls through to smaller <paramref name="gain"/>; a further tie falls
        /// through to smaller <paramref name="age"/>.
        /// </summary>
        static bool IsBetterVictim(int releasedTier, float gain, int age, int bestReleasedTier, float bestGain, int bestAge) {
            if (releasedTier != bestReleasedTier)
                return releasedTier < bestReleasedTier;
            if (gain != bestGain)
                return gain < bestGain;
            return age < bestAge;
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

                Span<float> sendSlice = perChannelReverb ? sendBus.AsSpan(0, frames * channels) : Span<float>.Empty;
                if (perChannelReverb)
                    sendSlice.Clear();

                Span<float> chorusSendSlice = perChannelChorus ? chorusSendBus.AsSpan(0, frames * channels) : Span<float>.Empty;
                if (perChannelChorus)
                    chorusSendSlice.Clear();

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

                        if (perChannelReverb) {
                            float sendWeight = Clamp(channelReverbSend[slot.Channel] + slot.Voice.ReverbSend, 0f, 1f);
                            if (sendWeight != 0f) {
                                for (int frame = 0; frame < frames; frame++) {
                                    float pre = scratchSlice[frame] * channelGainBlock[channelBase + frame] * sendWeight;
                                    int baseIndex = frame * channels;
                                    sendSlice[baseIndex] += pre * leftGain;
                                    sendSlice[baseIndex + 1] += pre * rightGain;
                                }
                            }
                        }

                        if (perChannelChorus) {
                            float chorusSendWeight = Clamp(channelChorusSend[slot.Channel] + slot.Voice.ChorusSend, 0f, 1f);
                            if (chorusSendWeight != 0f) {
                                for (int frame = 0; frame < frames; frame++) {
                                    float pre = scratchSlice[frame] * channelGainBlock[channelBase + frame] * chorusSendWeight;
                                    int baseIndex = frame * channels;
                                    chorusSendSlice[baseIndex] += pre * leftGain;
                                    chorusSendSlice[baseIndex + 1] += pre * rightGain;
                                }
                            }
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
                        if (slot.PendingChannel != NoPendingNote)
                            StartVoiceInSlot(v, slot.PendingChannel, slot.PendingKey, slot.PendingVelocity);
                        else {
                            slot.IsOccupied = false;
                            slot.Voice = null;
                        }
                    }
                }

                if (chorus != null) {
                    if (options.GlobalChorus)
                        chorus.Process(masterSlice, masterSlice);
                    else
                        chorus.Process(chorusSendSlice, masterSlice);
                }

                if (reverb != null) {
                    if (options.GlobalReverb)
                        reverb.Process(masterSlice, masterSlice);
                    else
                        reverb.Process(sendSlice, masterSlice);
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
        /// Stateless per-sample master-bus stage, before <see cref="Finalize"/> (INV-2): applies the
        /// <see cref="MasterHeadroomTrim"/> headroom trim, then a soft-clip on the trimmed value — unity
        /// below the knee, <c>tanh</c>-saturating toward ±1 above it; NaN/Inf samples pass through untouched
        /// (never trimmed, so <see cref="Finalize"/> remains their sole choke).
        /// </summary>
        static void ApplyMasterBus(Span<float> block) {
            for (int i = 0; i < block.Length; i++) {
                float x = block[i];
                if (float.IsNaN(x) || float.IsInfinity(x))
                    continue;

                float trimmed = x * MasterHeadroomTrim;
                float magnitude = Math.Abs(trimmed);
                if (magnitude <= MasterBusKneeThreshold) {
                    block[i] = trimmed;
                    continue;
                }

                float sign = trimmed < 0f ? -1f : 1f;
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

using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Audio;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Pull-based voice engine implementing <see cref="ISynthesizer"/>: turns MIDI-style note events into
    /// voices, mixes them to stereo or mono with per-channel gain/pan/pitch-bend/mod-wheel/sustain, and
    /// runs them through optional send-bus reverb and chorus. Supports voice-stealing when the pool is
    /// full, SF2 exclusive-class choking (generator 57), and multi-layer note-on via
    /// <see cref="IMultiVoicePatch"/> for SF2 zone/layer stacking. All buffers are ctor-sized; steady-state
    /// <see cref="Read"/> allocates nothing.
    /// </summary>
    public sealed class Synthesizer : ISynthesizer {

        const int ChannelCount = 16;

        /// <summary>
        /// Master-bus soft-clip knee: material at or below this magnitude passes through unchanged;
        /// above it, <see cref="ApplyMasterBus"/> compresses toward the ±1 ceiling.
        /// </summary>
        const float MasterBusKneeThreshold = 0.9f;

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
        readonly List<IVoice> layerVoiceBuffer;
        readonly List<int> layerSlotBuffer;
        int nextAge;
        float masterGain;

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
            layerVoiceBuffer = new List<IVoice>(4);
            layerSlotBuffer = new List<int>(4);
            masterGain = options.MasterGain;
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

            if (channelPatch[channel] is IMultiVoicePatch multiVoicePatch) {
                StartLayeredNote(multiVoicePatch, channel, key, velocity);
                return;
            }

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
            slot.PendingVoice = null;
        }

        /// <summary>
        /// SF2 zone/layer stacking note-on: resolves every layer <paramref name="patch"/> wants via
        /// <see cref="IMultiVoicePatch.StartVoices"/> and places each in its own slot (free-first, else
        /// steal, excluding this note's own already-placed siblings from victim candidacy). A layer that
        /// finds no slot is dropped without disturbing its siblings. The exclusive-class choke then runs
        /// once for the whole group so stacked layers never choke each other.
        /// </summary>
        void StartLayeredNote(IMultiVoicePatch patch, int channel, int key, int velocity) {
            layerVoiceBuffer.Clear();
            patch.StartVoices(key, velocity, layerVoiceBuffer);
            if (layerVoiceBuffer.Count == 0)
                return;

            layerSlotBuffer.Clear();

            for (int i = 0; i < layerVoiceBuffer.Count; i++) {
                IVoice voice = layerVoiceBuffer[i];

                int freeSlot = FindFreeSlot();
                if (freeSlot >= 0) {
                    PlaceVoiceInSlot(freeSlot, channel, key, voice);
                    layerSlotBuffer.Add(freeSlot);
                    continue;
                }

                int victim = FindStealVictim(layerSlotBuffer);
                if (victim < 0)
                    continue;

                ref VoiceSlot slot = ref pool[victim];
                slot.Voice!.FastFadeForSteal();
                slot.PendingChannel = channel;
                slot.PendingKey = key;
                slot.PendingVelocity = velocity;
                slot.PendingVoice = voice;
                layerSlotBuffer.Add(victim);
            }

            ChokeSameClassVoicesForNote(channel, layerSlotBuffer);
        }

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) {
            bool sustained = channelSustain[channel];
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel && slot.Key == key)
                    ReleaseSlot(ref slot, sustained);
            }
        }

        /// <inheritdoc/>
        public void SilenceChannel(int channel) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (!slot.IsOccupied)
                    continue;

                // slot.Channel (current voice) and slot.PendingChannel (a note parked behind a steal fade)
                // are checked independently -- either, both, or neither may match this channel.
                if (slot.Channel == channel)
                    slot.Voice!.FastFadeForSteal();

                if (slot.PendingChannel == channel) {
                    slot.PendingChannel = NoPendingNote;
                    // Read's consumption check tests PendingVoice first, so a cancelled layer must also
                    // clear it here or it would resurrect after this call.
                    slot.PendingVoice = null;
                }
            }
        }

        /// <inheritdoc/>
        public void ReleaseAllNotes(int channel) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            bool sustained = channelSustain[channel];
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel)
                    ReleaseSlot(ref slot, sustained);
            }
        }

        /// <inheritdoc/>
        public void SetMasterGain(float gain) {
            if (float.IsNaN(gain) || gain < 0f)
                throw new ArgumentOutOfRangeException(nameof(gain), gain, "Master gain must be non-negative and non-NaN.");
            masterGain = gain;
        }

        /// <summary>
        /// Shared sustain-aware release branch for <see cref="NoteOff"/> and <see cref="ReleaseAllNotes"/>:
        /// while <paramref name="sustained"/> the slot's release is deferred (<see cref="VoiceSlot.PendingRelease"/>),
        /// otherwise the voice releases into its normal envelope tail immediately.
        /// </summary>
        static void ReleaseSlot(ref VoiceSlot slot, bool sustained) {
            if (sustained) {
                slot.PendingRelease = true;
            } else {
                slot.Voice!.Release();
                slot.Released = true;
            }
        }

        /// <summary>
        /// Starts a new voice for <paramref name="channel"/>/<paramref name="key"/>/<paramref name="velocity"/>
        /// in <paramref name="slotIndex"/>, applying live pitch-bend/mod-wheel and running the
        /// exclusive-class choke. Shared by the free-slot path and the deferred pending-note start in
        /// <see cref="Read"/>.
        /// </summary>
        void StartVoiceInSlot(int slotIndex, int channel, int key, int velocity) {
            IVoice voice = channelPatch[channel].StartVoice(key, velocity);
            PlaceVoiceInSlot(slotIndex, channel, key, voice);
            ChokeSameClassVoices(slotIndex, channel, voice.ExclusiveClass);
        }

        /// <summary>
        /// Places an already-constructed <paramref name="voice"/> into <paramref name="slotIndex"/>,
        /// applying live pitch-bend/mod-wheel and stamping a fresh age. Shared by
        /// <see cref="StartVoiceInSlot"/> and <see cref="StartLayeredNote"/>. Does not run the
        /// exclusive-class choke — callers choke individually or once for the whole note, so a
        /// multi-layer note can defer choking until every layer is placed.
        /// </summary>
        void PlaceVoiceInSlot(int slotIndex, int channel, int key, IVoice voice) {
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
            slot.PendingVoice = null;
        }

        /// <summary>
        /// SF2 exclusive-class choke (generator 57): when <paramref name="exclusiveClass"/> is non-zero,
        /// fast-fades every other occupied, non-draining slot on <paramref name="channel"/> sharing that
        /// class. A class of 0 is a no-op.
        /// </summary>
        void ChokeSameClassVoices(int slotIndex, int channel, int exclusiveClass) {
            if (exclusiveClass == 0)
                return;

            for (int i = 0; i < pool.Length; i++) {
                if (i == slotIndex)
                    continue;

                ref VoiceSlot other = ref pool[i];
                if (!other.IsOccupied || other.PendingChannel != NoPendingNote)
                    continue;
                if (other.Channel != channel || other.Voice!.ExclusiveClass != exclusiveClass)
                    continue;

                other.Voice.FastFadeForSteal();
            }
        }

        /// <summary>
        /// Sibling-aware exclusive-class choke for a note that started multiple layers in one
        /// <see cref="StartLayeredNote"/> call: for each slot in <paramref name="noteSlots"/> carrying a
        /// non-zero exclusive class, fast-fades every other occupied, non-draining, same-channel slot
        /// sharing that class — but never a slot in <paramref name="noteSlots"/> itself, so stacked layers
        /// of this note never choke each other. External same-class voices are still choked normally.
        /// </summary>
        void ChokeSameClassVoicesForNote(int channel, List<int> noteSlots) {
            for (int n = 0; n < noteSlots.Count; n++) {
                int slotIndex = noteSlots[n];
                ref VoiceSlot slot = ref pool[slotIndex];
                // PendingVoice (the incoming layer) is checked first: for a stolen slot, the victim is
                // still occupying it and fading, so slot.Voice would be the wrong layer's class.
                int exclusiveClass = slot.PendingVoice != null
                    ? slot.PendingVoice.ExclusiveClass
                    : slot.Voice?.ExclusiveClass ?? 0;

                if (exclusiveClass == 0)
                    continue;

                for (int i = 0; i < pool.Length; i++) {
                    if (noteSlots.Contains(i))
                        continue;

                    ref VoiceSlot other = ref pool[i];
                    if (!other.IsOccupied || other.PendingChannel != NoPendingNote)
                        continue;
                    if (other.Channel != channel || other.Voice!.ExclusiveClass != exclusiveClass)
                        continue;

                    other.Voice.FastFadeForSteal();
                }
            }
        }

        /// <summary>
        /// Scans the occupied, non-draining slots for the best voice-stealing victim: the smallest
        /// lexicographic tuple <c>(releasedTier, currentGain, age)</c> — released voices die first, then
        /// the quietest, then the oldest. Slots already holding a pending note are excluded. Returns -1
        /// when every occupied slot is already draining.
        /// </summary>
        /// <param name="excludedSlots">
        /// optional extra pool indices to exclude from victim candidacy, used by
        /// <see cref="StartLayeredNote"/> to protect a note's own already-placed sibling layers; <c>null</c>
        /// applies no extra exclusion.
        /// </param>
        int FindStealVictim(List<int>? excludedSlots = null) {
            int best = -1;
            int bestReleasedTier = 0;
            float bestGain = 0f;
            int bestAge = 0;

            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (!slot.IsOccupied || slot.PendingChannel != NoPendingNote)
                    continue;
                if (excludedSlots != null && excludedSlots.Contains(i))
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
                        if (slot.PendingVoice != null) {
                            // Already built by StartLayeredNote; place it directly instead of re-resolving
                            // via IPatch.StartVoice, which could pick a different layer.
                            IVoice pendingVoice = slot.PendingVoice;
                            int pendingChannel = slot.PendingChannel;
                            int pendingKey = slot.PendingKey;
                            PlaceVoiceInSlot(v, pendingChannel, pendingKey, pendingVoice);
                            ChokeSameClassVoices(v, pendingChannel, pendingVoice.ExclusiveClass);
                        } else if (slot.PendingChannel != NoPendingNote) {
                            StartVoiceInSlot(v, slot.PendingChannel, slot.PendingKey, slot.PendingVelocity);
                        } else {
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
        /// <see cref="channelGainBlock"/>, before any voice is mixed.
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
        /// Applies <see cref="masterGain"/> to the master bus, then soft-clips it before <see cref="Finalize"/>
        /// (INV-2): unity below the knee, <c>tanh</c>-saturating toward ±1 above it; NaN/Inf samples pass
        /// through untouched. At unity gain the clip loop below runs byte-for-byte as before (the gain
        /// multiply is skipped entirely rather than relying on a ×1f no-op).
        /// </summary>
        void ApplyMasterBus(Span<float> block) {
            if (masterGain != 1f) {
                for (int i = 0; i < block.Length; i++)
                    block[i] *= masterGain;
            }

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

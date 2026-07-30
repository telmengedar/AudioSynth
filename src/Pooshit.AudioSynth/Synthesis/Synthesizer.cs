using System;
using System.Collections.Generic;
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
    /// exceeds the pool never takes this path and renders bit-for-bit as before. Also implements SF2
    /// exclusive-class choking (generator 57, DiVoid #7226/#7227): starting a voice whose region carries a
    /// non-zero <see cref="IVoice.ExclusiveClass"/> fast-fades every other sounding, non-draining voice on
    /// the same channel sharing that class (e.g. GM hi-hats), reusing the same click-free
    /// <see cref="IVoice.FastFadeForSteal"/> voice-stealing already ships; a voice with class 0 — every
    /// non-SF2 voice and every SF2 region without gen 57 — takes today's path unchanged, bit-for-bit. All
    /// buffers, including both effects' delay lines and send buses, are ctor-sized; steady-state
    /// <see cref="Read"/> allocates nothing. Also implements SF2 zone/layer stacking (DiVoid #7282):
    /// when a channel's patch is an <see cref="IMultiVoicePatch"/>, <see cref="NoteOn"/> starts every
    /// layer the patch resolves and gives each its own pool slot, all sharing <c>(channel, key)</c> so
    /// <see cref="NoteOff"/>/sustain/<see cref="SilenceChannel"/>/<see cref="ReleaseAllNotes"/> release
    /// every layer together through this same unchanged per-slot machinery; a plain <see cref="IPatch"/>
    /// (every non-SF2 patch, and every test/demo <see cref="IPatch"/> helper) takes today's single-voice
    /// path completely unchanged. Layer placement is per-layer free-slot-else-steal with sibling
    /// protection (a note's own already-placed layers are never cannibalised by its later layers), and
    /// the gen-57 exclusive-class choke runs once after all of a note's layers are placed, excluding the
    /// note's own siblings, so stacked layers never choke each other.
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
        /// SF2 zone/layer stacking onset (DiVoid #7282 §6.1/§6.2): resolves every layer
        /// <paramref name="patch"/> wants for this note-on via <see cref="IMultiVoicePatch.StartVoices"/>
        /// into the reusable <see cref="layerVoiceBuffer"/>, then places each layer in its own slot —
        /// free-first, else steal the standard <c>(releasedTier, currentGain, age)</c> victim, excluding
        /// this note's own already-placed/already-committed sibling slots (<see cref="layerSlotBuffer"/>)
        /// from victim candidacy so a note can never cannibalise itself. A layer that finds neither a
        /// free slot nor an eligible victim is dropped (partial, graceful stealing — earlier layers of
        /// the same note keep sounding); this only happens when simultaneous layers across all in-flight
        /// notes exceed the pool size, a pathological/exhaustion case, not ordinary operation. Once every
        /// layer has been placed or dropped, the gen-57 exclusive-class choke runs exactly once,
        /// excluding the whole sibling group (<see cref="ChokeSameClassVoicesForNote"/>), so stacked
        /// layers of this note never choke each other.
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

                // Two independent conditions on the same slot (task #7249, W1): the slot's CURRENT voice
                // (slot.Channel) and a note PARKED behind its steal fade (slot.PendingChannel) can target
                // different channels, so each must be checked on its own -- a slot may match one, both, or
                // neither. Gating the pending-cancel on slot.Channel==channel (as before) could drop a
                // parked note bound for a different channel (over-cancel) or miss one bound for THIS
                // channel when it sits behind a different channel's fading victim, letting it resurrect
                // after All Sound Off (under-cancel, the exact resurrection this method exists to prevent).
                if (slot.Channel == channel)
                    slot.Voice!.FastFadeForSteal();

                if (slot.PendingChannel == channel) {
                    slot.PendingChannel = NoPendingNote;
                    // Cancel a pre-built pending layer too (SF2 zone/layer stacking, DiVoid #7282): Read's
                    // consumption check tests PendingVoice first, so leaving it non-null here would let a
                    // cancelled layer resurrect after All Sound Off exactly like the PendingChannel bug
                    // this method's cancellation already guards against.
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
        /// in <paramref name="slotIndex"/>, applying the channel's live pitch-bend and mod-wheel exactly as
        /// a fresh <see cref="NoteOn"/> does, and stamping a fresh age. Shared by the free-slot path and the
        /// deferred pending-note start (<see cref="Read"/>) so both allocate a voice identically, and so
        /// both onsets get the exclusive-class choke (DiVoid #7226/#7227): a non-zero
        /// <see cref="IVoice.ExclusiveClass"/> fast-fades every other occupied, non-draining, same-channel
        /// voice sharing that class.
        /// </summary>
        void StartVoiceInSlot(int slotIndex, int channel, int key, int velocity) {
            IVoice voice = channelPatch[channel].StartVoice(key, velocity);
            PlaceVoiceInSlot(slotIndex, channel, key, voice);
            ChokeSameClassVoices(slotIndex, channel, voice.ExclusiveClass);
        }

        /// <summary>
        /// Places an already-constructed <paramref name="voice"/> into <paramref name="slotIndex"/>,
        /// applying the channel's live pitch-bend and mod-wheel and stamping a fresh age — the shared
        /// tail of both <see cref="StartVoiceInSlot"/> (which constructs the voice itself, immediately
        /// before calling this) and SF2 zone/layer stacking's <see cref="StartLayeredNote"/> (which
        /// constructs every layer's voice up front via <see cref="IMultiVoicePatch.StartVoices"/> and
        /// places each one separately). Does NOT run the exclusive-class choke — callers choke
        /// individually (<see cref="StartVoiceInSlot"/>) or once for the whole note
        /// (<see cref="ChokeSameClassVoicesForNote"/>), never here, so a multi-layer note can defer
        /// choking until every layer is placed.
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
        /// SF2 exclusive-class choke (gen 57, DiVoid #7226/#7227): when <paramref name="exclusiveClass"/>
        /// is non-zero, fast-fades every other occupied, non-draining slot on <paramref name="channel"/>
        /// whose voice reports the same class — the click-free cut voice-stealing already ships (INV-1).
        /// <c>exclusiveClass == 0</c> returns immediately, so non-choke content takes today's path
        /// unchanged, bit-for-bit. Single linear pool scan, no allocation (INV-2).
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
        /// <see cref="StartLayeredNote"/> call (SF2 zone/layer stacking, DiVoid #7282 §9.2): for each
        /// layer's slot in <paramref name="noteSlots"/> that carries a non-zero exclusive class, fast-fades
        /// every OTHER occupied, non-draining, same-channel slot sharing that class — but never a slot in
        /// <paramref name="noteSlots"/> itself, so stacked layers of this note never choke each other.
        /// External same-class voices (e.g. a prior hi-hat hit) are still choked exactly as
        /// <see cref="ChokeSameClassVoices"/> does for a single-voice note. A layer that was dropped under
        /// steal exhaustion is simply absent from <paramref name="noteSlots"/> and neither triggers nor
        /// receives a choke from this call.
        /// </summary>
        /// <remarks>
        /// Scoped to THIS call's placements only: a layer parked behind a steal (<see cref="VoiceSlot.PendingVoice"/>)
        /// that later starts sounding on its own, asynchronously, in <see cref="Read"/> is placed via the
        /// ordinary <see cref="ChokeSameClassVoices"/> path and does not re-run this sibling exclusion —
        /// an accepted, narrow limitation only reachable when the pool is already exhausted AND stacked
        /// layers of one note share a non-zero exclusive class (DiVoid #7282 does not specify behaviour
        /// for this compound edge case).
        /// </remarks>
        /// <remarks>
        /// Jenny's rev3 review (#7287, Focus #3) flagged the prior <c>slot.IsOccupied ? slot.Voice!.ExclusiveClass
        /// : slot.PendingVoice?.ExclusiveClass</c> ordering as reading the WRONG layer's class for a
        /// stolen slot: the victim voice is still fading in place, so a stolen sibling slot is always
        /// <see cref="VoiceSlot.IsOccupied"/>, making the <c>PendingVoice</c> branch dead code and this
        /// method choke on the victim's class instead of the incoming layer's. Fixed by keying on
        /// <see cref="VoiceSlot.PendingVoice"/> first: a stolen slot always has one (the incoming layer,
        /// set just before this call in <see cref="StartLayeredNote"/>), while a freshly-placed free-slot
        /// layer never does (<see cref="PlaceVoiceInSlot"/> clears it), so its already-current
        /// <see cref="VoiceSlot.Voice"/> IS the incoming layer.
        /// </remarks>
        void ChokeSameClassVoicesForNote(int channel, List<int> noteSlots) {
            for (int n = 0; n < noteSlots.Count; n++) {
                int slotIndex = noteSlots[n];
                ref VoiceSlot slot = ref pool[slotIndex];
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
        /// lexicographic tuple <c>(releasedTier, currentGain, age)</c> — a released voice (immediate
        /// release or a sustain-deferred release) dies before any still-held voice; among voices tied on
        /// that tier the quietest dies first; age breaks remaining ties toward the oldest. Slots already
        /// holding a pending note are excluded — they are already committed to a steal and must not lose
        /// it. Returns -1 when every occupied slot is already draining (pathological: more note-ons than
        /// the pool has slots inside one render).
        /// </summary>
        /// <param name="excludedSlots">
        /// optional pool indices to exclude from victim candidacy in addition to the standard pending-note
        /// exclusion — used by SF2 zone/layer stacking (<see cref="StartLayeredNote"/>, DiVoid #7282 §9.1)
        /// to protect a note's own already-placed sibling layers from being cannibalised by its later
        /// layers. <c>null</c> (the default, used by every non-stacking call site) applies no extra exclusion.
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
                            // SF2 zone/layer stacking (DiVoid #7282 §6.2): this layer's voice was already
                            // built up front by StartLayeredNote, so placement just needs the slot's
                            // remembered channel/key -- no fresh IPatch.StartVoice call, and no risk of
                            // re-resolving a different (wrong) layer.
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

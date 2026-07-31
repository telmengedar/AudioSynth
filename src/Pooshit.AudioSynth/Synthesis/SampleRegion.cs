using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable descriptor of a playable mono sample region: buffer reference, loop boundaries,
    /// source sample rate, root key, and pitch correction; runtime playback state lives in the voice.
    /// </summary>
    public sealed class SampleRegion {

        /// <summary>
        /// Creates a <see cref="SampleRegion"/>.
        /// </summary>
        /// <param name="buffer">mono float PCM samples shared across all voices that play this region</param>
        /// <param name="start">inclusive start index into <paramref name="buffer"/></param>
        /// <param name="end">exclusive end index into <paramref name="buffer"/></param>
        /// <param name="loopStart">inclusive loop-start index; ignored when <see cref="LoopMode"/> is <see cref="LoopMode.NoLoop"/></param>
        /// <param name="loopEnd">exclusive loop-end index; ignored when <see cref="LoopMode"/> is <see cref="LoopMode.NoLoop"/></param>
        /// <param name="loopMode">loop behaviour for this region</param>
        /// <param name="sourceSampleRate">sample rate of the source recording in frames per second</param>
        /// <param name="rootKey">MIDI key number at which the sample plays at its original pitch (0–127)</param>
        /// <param name="pitchCorrectionCents">fine-tuning offset in cents applied on top of the key transposition</param>
        /// <param name="envelope">volume-envelope parameters shaping this region's amplitude over the note's life</param>
        /// <param name="filter">low-pass filter parameters shaping this region's timbre before the amplifier</param>
        /// <param name="lfo">modulation-LFO parameters driving this region's vibrato over the note's life</param>
        /// <param name="pan">static per-voice stereo position in [-1,1] (-1 = full left, 0 = centre, +1 = full right), sourced from SF2 generator 17</param>
        /// <param name="reverbSend">
        /// static per-voice reverb-send weight in [0,1], sourced from SF2 generator 16 (reverbEffectsSend);
        /// defaults to <c>1.0</c> (neutral pass-through — a region without an explicit gen-16 lets the
        /// channel's <see cref="ISynthesizer.SetChannelReverbSend"/> weight drive fully) rather than SF2's
        /// literal gen-16 default of 0, so CC91 is never impotent for regions that omit the generator.
        /// </param>
        /// <param name="chorusSend">
        /// static per-voice chorus-send weight in [0,1], sourced from SF2 generator 15 (chorusEffectsSend);
        /// defaults to <c>0f</c>, matching both the SF2 spec's literal gen-15 default and the additive
        /// combination's neutral element, so a region without an explicit gen-15 contributes no bias and
        /// the channel's <see cref="ISynthesizer.SetChannelChorusSend"/> weight alone still drives the
        /// voice (no impotence special-case, unlike <paramref name="reverbSend"/>'s inherited 1f).
        /// </param>
        /// <param name="exclusiveClass">
        /// SF2 generator 57 (exclusiveClass) value; defaults to <c>0</c>, meaning the region belongs to
        /// no choke group. A non-zero value names a choke group: starting a voice for this region silences
        /// every other sounding voice sharing the same class on the same channel (SF2 spec; e.g. GM
        /// hi-hats), a click-free choke the engine implements by reusing <see cref="IVoice.FastFadeForSteal"/>.
        /// </param>
        /// <param name="initialAttenuationGain">
        /// static per-region linear amplitude multiplier sourced from SF2 generator 48 (InitialAttenuation),
        /// summed additively across the preset-zone and instrument-zone levels and converted from centibels
        /// via <c>10^(-cB/200)</c>; defaults to <c>1f</c> (no attenuation), matching the gain an absent
        /// gen-48 at both levels produces. Applied as a fixed multiplier alongside — not in place of —
        /// velocity gain, channel gain, pan, and the volume envelope.
        /// </param>
        /// <param name="modEnv">
        /// key-independent modulation-envelope parameters (SF2 gens 25, 26, 29, 30 plus gens 27/28 resolved
        /// as if key 60); defaults to <see cref="ModulationEnvelopeParameters.Default"/>. Hold and decay are
        /// re-resolved per note from <paramref name="modEnvHoldTimecents"/>/<paramref name="modEnvDecayTimecents"/>
        /// (see <see cref="Patches.SamplePatch.StartVoice"/>), since they depend on the played key.
        /// </param>
        /// <param name="modEnvHoldTimecents">raw effective mod-envelope hold time (SF2 gen-27, timecents) before keynum scaling</param>
        /// <param name="modEnvDecayTimecents">raw effective mod-envelope decay time (SF2 gen-28, timecents) before keynum scaling</param>
        /// <param name="modEnvHoldKeynumCents">keynum-to-mod-envelope-hold coefficient (SF2 gen-31, timecents/key)</param>
        /// <param name="modEnvDecayKeynumCents">keynum-to-mod-envelope-decay coefficient (SF2 gen-32, timecents/key)</param>
        /// <param name="modEnvToPitchCents">peak pitch deviation in cents at full modulation-envelope excursion (SF2 gen-7); 0 is inert</param>
        public SampleRegion(
            float[] buffer,
            int start,
            int end,
            int loopStart,
            int loopEnd,
            LoopMode loopMode,
            int sourceSampleRate,
            int rootKey,
            int pitchCorrectionCents,
            EnvelopeParameters envelope,
            FilterParameters filter,
            LfoParameters lfo,
            float pan,
            float reverbSend = 1f,
            float chorusSend = 0f,
            int exclusiveClass = 0,
            float initialAttenuationGain = 1f,
            ModulationEnvelopeParameters? modEnv = null,
            float modEnvHoldTimecents = -12000f,
            float modEnvDecayTimecents = -12000f,
            float modEnvHoldKeynumCents = 0f,
            float modEnvDecayKeynumCents = 0f,
            float modEnvToPitchCents = 0f) {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (start < 0 || start >= buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (end <= start || end > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(end));
            if (loopMode == LoopMode.Continuous) {
                if (loopStart < start || loopStart >= end)
                    throw new ArgumentOutOfRangeException(nameof(loopStart));
                if (loopEnd <= loopStart || loopEnd > end)
                    throw new ArgumentOutOfRangeException(nameof(loopEnd));
            }
            if (sourceSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            if (rootKey < 0 || rootKey > 127)
                throw new ArgumentOutOfRangeException(nameof(rootKey));
            Buffer = buffer;
            Start = start;
            End = end;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
            LoopMode = loopMode;
            SourceSampleRate = sourceSampleRate;
            RootKey = rootKey;
            PitchCorrectionCents = pitchCorrectionCents;
            Envelope = envelope;
            Filter = filter;
            Lfo = lfo;
            Pan = pan;
            ReverbSend = reverbSend;
            ChorusSend = chorusSend;
            ExclusiveClass = exclusiveClass;
            InitialAttenuationGain = initialAttenuationGain;
            ModEnv = modEnv ?? ModulationEnvelopeParameters.Default;
            ModEnvHoldTimecents = modEnvHoldTimecents;
            ModEnvDecayTimecents = modEnvDecayTimecents;
            ModEnvHoldKeynumCents = modEnvHoldKeynumCents;
            ModEnvDecayKeynumCents = modEnvDecayKeynumCents;
            ModEnvToPitchCents = modEnvToPitchCents;
        }

        /// <summary>
        /// Mono PCM sample data shared across all voices that play this region.
        /// </summary>
        public float[] Buffer { get; }

        /// <summary>
        /// Inclusive start index into <see cref="Buffer"/>.
        /// </summary>
        public int Start { get; }

        /// <summary>
        /// Exclusive end index into <see cref="Buffer"/>.
        /// </summary>
        public int End { get; }

        /// <summary>
        /// Inclusive loop-start index; relevant only when <see cref="LoopMode"/> is <see cref="LoopMode.Continuous"/>.
        /// </summary>
        public int LoopStart { get; }

        /// <summary>
        /// Exclusive loop-end index; relevant only when <see cref="LoopMode"/> is <see cref="LoopMode.Continuous"/>.
        /// </summary>
        public int LoopEnd { get; }

        /// <summary>
        /// Loop behaviour: no-loop one-shot or continuous looping between loop points.
        /// </summary>
        public LoopMode LoopMode { get; }

        /// <summary>
        /// Sample rate of the source recording in frames per second.
        /// </summary>
        public int SourceSampleRate { get; }

        /// <summary>
        /// MIDI key number at which the sample plays at its original pitch (0–127).
        /// </summary>
        public int RootKey { get; }

        /// <summary>
        /// Fine-tuning offset in cents added to the key transposition.
        /// </summary>
        public int PitchCorrectionCents { get; }

        /// <summary>
        /// Volume-envelope parameters shaping this region's amplitude over the note's life.
        /// </summary>
        public EnvelopeParameters Envelope { get; }

        /// <summary>
        /// Low-pass filter parameters shaping this region's timbre before the amplifier stage.
        /// </summary>
        public FilterParameters Filter { get; }

        /// <summary>
        /// Modulation-LFO parameters driving this region's vibrato (pitch modulation) over the note's life.
        /// </summary>
        public LfoParameters Lfo { get; }

        /// <summary>
        /// Static per-voice stereo position in [-1,1] (-1 = full left, 0 = centre, +1 = full right),
        /// sourced from SF2 generator 17 and combined with the channel's dynamic pan at mix time.
        /// </summary>
        public float Pan { get; }

        /// <summary>
        /// Static per-voice reverb-send weight in [0,1], sourced from SF2 generator 16 and combined
        /// additively with the channel's dynamic reverb-send weight at mix time (clamped to [0,1]).
        /// </summary>
        public float ReverbSend { get; }

        /// <summary>
        /// Static per-voice chorus-send weight in [0,1], sourced from SF2 generator 15 and combined
        /// additively with the channel's dynamic chorus-send weight at mix time.
        /// </summary>
        public float ChorusSend { get; }

        /// <summary>
        /// SF2 generator 57 (exclusiveClass) value; <c>0</c> means the region belongs to no choke group.
        /// A non-zero value names a choke group matched, at note-onset, within the same MIDI channel.
        /// </summary>
        public int ExclusiveClass { get; }

        /// <summary>
        /// Static per-region linear amplitude multiplier sourced from SF2 generator 48 (InitialAttenuation),
        /// summed additively across the preset-zone and instrument-zone levels; defaults to <c>1.0</c>
        /// (no attenuation) when the generator is absent at both levels. Combined multiplicatively with
        /// velocity gain to form the voice's target gain (<see cref="Patches.SamplePatch.StartVoice"/>) —
        /// applied alongside, not in place of, channel gain, pan, and the volume envelope.
        /// </summary>
        public float InitialAttenuationGain { get; }

        /// <summary>
        /// Key-independent modulation-envelope parameters; hold/decay are resolved as if key 60 and are
        /// re-resolved per note by <see cref="Patches.SamplePatch.StartVoice"/> using
        /// <see cref="ModEnvHoldTimecents"/>/<see cref="ModEnvDecayTimecents"/> and the keynum coefficients.
        /// </summary>
        public ModulationEnvelopeParameters ModEnv { get; }

        /// <summary>Raw effective mod-envelope hold time (SF2 gen-27, timecents) before keynum scaling.</summary>
        public float ModEnvHoldTimecents { get; }

        /// <summary>Raw effective mod-envelope decay time (SF2 gen-28, timecents) before keynum scaling.</summary>
        public float ModEnvDecayTimecents { get; }

        /// <summary>Keynum-to-mod-envelope-hold coefficient (SF2 gen-31, timecents/key).</summary>
        public float ModEnvHoldKeynumCents { get; }

        /// <summary>Keynum-to-mod-envelope-decay coefficient (SF2 gen-32, timecents/key).</summary>
        public float ModEnvDecayKeynumCents { get; }

        /// <summary>Peak pitch deviation, in cents, at full modulation-envelope excursion (SF2 gen-7); zero is inert.</summary>
        public float ModEnvToPitchCents { get; }
    }
}

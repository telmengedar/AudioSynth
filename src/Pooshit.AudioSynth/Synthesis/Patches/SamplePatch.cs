using System;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Synthesis.Patches {

    /// <summary>
    /// <see cref="IPatch"/> that plays a fixed mono <see cref="SampleRegion"/>; <see cref="StartVoice"/>
    /// computes the pitch increment for the played key, a concave <c>(velocity/127)²</c>
    /// velocity-to-gain mapping (<see cref="VelocityToGain"/>) scaled by the region's static
    /// <see cref="SampleRegion.InitialAttenuationGain"/> (SF2 generator 48), resolves the key/velocity-
    /// dependent filter and modulation-envelope values the cached (key/velocity-independent) region cannot
    /// carry — the velocity-to-filter-cutoff offset (SF2 §8.4.2) and the keynum-adjusted mod-envelope
    /// hold/decay (gens 31/32) — then constructs a <see cref="SamplePlaybackVoice"/> ready to render.
    /// </summary>
    public sealed class SamplePatch : IPatch {

        /// <summary>Full-scale value for a 7-bit MIDI velocity.</summary>
        const float VelocityFullScale = 127f;

        /// <summary>SF2 §8.4.2 default velocity-to-filter-cutoff modulator range, in cents.</summary>
        const float VelocityFilterCutoffRangeCents = -2400f;

        /// <summary>Envelope-time ceiling matching <see cref="Formats.Sf2.Sf2RegionResolver"/>'s.</summary>
        const float MaxEnvelopeSeconds = 20f;

        readonly SampleRegion region;
        readonly int outputSampleRate;

        /// <summary>
        /// Creates a <see cref="SamplePatch"/>.
        /// </summary>
        /// <param name="region">the sample region to play for every note</param>
        /// <param name="outputSampleRate">engine output sample rate used to compute the pitch increment</param>
        public SamplePatch(SampleRegion region, int outputSampleRate) {
            this.region = region ?? throw new ArgumentNullException(nameof(region));
            if (outputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            this.outputSampleRate = outputSampleRate;
        }

        /// <summary>
        /// Starts a <see cref="SamplePlaybackVoice"/> for the given note and velocity.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127); mapped to gain by the concave <see cref="VelocityToGain"/> curve</param>
        public IVoice StartVoice(int key, int velocity) {
            double semitones = (key - region.RootKey) + region.PitchCorrectionCents / 100.0;
            float pitchIncrement = (float)(Math.Pow(2.0, semitones / 12.0) * region.SourceSampleRate / (double)outputSampleRate);
            float targetGain = VelocityToGain(velocity) * region.InitialAttenuationGain;

            float effectiveBaseCutoffCents = region.Filter.BaseCutoffCents + VelocityToFilterCutoffOffsetCents(velocity);

            float holdTimecents = region.ModEnvHoldTimecents + region.ModEnvHoldKeynumCents * (60 - key);
            float decayTimecents = region.ModEnvDecayTimecents + region.ModEnvDecayKeynumCents * (60 - key);
            ModulationEnvelopeParameters modEnv = new ModulationEnvelopeParameters(
                region.ModEnv.DelaySeconds,
                region.ModEnv.AttackSeconds,
                TimecentsToSeconds(holdTimecents),
                TimecentsToSeconds(decayTimecents),
                region.ModEnv.SustainLevel,
                region.ModEnv.ReleaseSeconds);

            return new SamplePlaybackVoice(region, pitchIncrement, targetGain, outputSampleRate, effectiveBaseCutoffCents, modEnv);
        }

        /// <summary>
        /// Concave MIDI-velocity-to-linear-gain characteristic <c>(velocity/127)²</c>, the SF2/GM velocity
        /// response: soft notes are attenuated more than a linear reading (velocity 64 → ~0.25, not 0.5), so
        /// note-to-note dynamics read as intended. Shares the squared shape used for CC7/CC11 channel gain.
        /// </summary>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <returns>linear gain in [0,1]; 0 at velocity 0, 1 at velocity 127</returns>
        public static float VelocityToGain(int velocity) {
            float normalized = velocity / VelocityFullScale;
            return normalized * normalized;
        }

        /// <summary>
        /// SF2 §8.4.2 default velocity-to-filter-cutoff modulator: darkens soft notes by up to
        /// <see cref="VelocityFilterCutoffRangeCents"/> at velocity 0, with no offset at velocity 127.
        /// </summary>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <returns>cutoff offset in cents, additive with the region's base cutoff</returns>
        static float VelocityToFilterCutoffOffsetCents(int velocity) {
            int clamped = velocity < 0 ? 0 : velocity > 127 ? 127 : velocity;
            return VelocityFilterCutoffRangeCents * (VelocityFullScale - clamped) / VelocityFullScale;
        }

        static float TimecentsToSeconds(float timecents) {
            double seconds = Math.Pow(2.0, timecents / 1200.0);
            if (seconds > MaxEnvelopeSeconds)
                return MaxEnvelopeSeconds;
            if (seconds < 0d)
                return 0f;
            return (float)seconds;
        }
    }
}

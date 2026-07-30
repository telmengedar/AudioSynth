using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable, rate-independent description of a resonant low-pass filter: the cutoff frequency in
    /// hertz, the resonance as a linear quality factor (Q), and the modulation-envelope-to-cutoff depth
    /// in cents (SF2 gen-11) that a voice combines with its live modulation-envelope value every control
    /// tick. <see cref="BaseCutoffCents"/> exposes the same cutoff in the absolute-cents domain so a
    /// voice can combine base, LFO, and mod-env cutoff contributions additively before a single Hz
    /// conversion; the base cutoff is never collapsed to an open decision here — that decision is made
    /// per control tick on the effective (combined) cutoff by <see cref="BiquadLowPassFilter"/>.
    /// </summary>
    public readonly struct FilterParameters {

        /// <summary>Reference frequency (Hz) for SF2 absolute-cents conversion: 8.176 Hz = MIDI note 0.</summary>
        const float ReferenceHz = 8.176f;

        /// <summary>
        /// Creates a <see cref="FilterParameters"/>.
        /// </summary>
        /// <param name="cutoffHz">low-pass corner frequency in hertz; at or above <see cref="Sf2OpenCutoffHz"/> the filter is open</param>
        /// <param name="resonance">resonance as a linear quality factor (Q); 0.707 is a flat Butterworth response with no peak</param>
        /// <param name="modEnvToCutoffCents">peak cutoff deviation, in cents, at full modulation-envelope excursion (SF2 gen-11); 0 is inert</param>
        public FilterParameters(float cutoffHz, float resonance, float modEnvToCutoffCents = 0f) {
            CutoffHz = cutoffHz;
            Resonance = resonance;
            ModEnvToCutoffCents = modEnvToCutoffCents;
        }

        /// <summary>
        /// Low-pass corner frequency in hertz.  At or above <see cref="Sf2OpenCutoffHz"/> the filter is open.
        /// </summary>
        public float CutoffHz { get; }

        /// <summary>
        /// Resonance as a linear quality factor (Q); 0.707 is a flat Butterworth response with no resonant peak.
        /// </summary>
        public float Resonance { get; }

        /// <summary>
        /// Peak cutoff deviation, in cents, applied when the region's modulation envelope is at full
        /// excursion (SF2 gen-11); zero means the mod envelope does not affect the cutoff.
        /// </summary>
        public float ModEnvToCutoffCents { get; }

        /// <summary>
        /// <see cref="CutoffHz"/> re-expressed in the absolute-cents domain, for additive combination
        /// with LFO and mod-envelope cutoff contributions.
        /// </summary>
        public float BaseCutoffCents => 1200f * (float)Math.Log(CutoffHz / ReferenceHz, 2.0);

        /// <summary>
        /// Converts an absolute-cents value to hertz via the SF2 reference formula
        /// (<see cref="ReferenceHz"/> · 2^(cents/1200)); the inverse of <see cref="BaseCutoffCents"/>.
        /// </summary>
        public static float CentsToHz(float cents) => (float)(ReferenceHz * Math.Pow(2.0, cents / 1200.0));

        /// <summary>
        /// The SF2-specification default filter: an open cutoff (13500 absolute cents ≈ 19913 Hz) with no
        /// resonance.  Realised as an exact passthrough, so hand-built patches and any region whose SF2
        /// initial-filter generators are absent are unaffected by filtering.
        /// </summary>
        public static FilterParameters Default =>
            new FilterParameters(Sf2OpenCutoffHz, ButterworthResonance);

        /// <summary>
        /// Cutoff frequency in hertz corresponding to the SF2 default initial-filter cutoff of 13500
        /// absolute cents (8.176 · 2^(13500/1200) ≈ 19913 Hz).  A requested cutoff at or above this value
        /// denotes an open filter and is realised as a passthrough at any sample rate.
        /// </summary>
        public static readonly float Sf2OpenCutoffHz = CentsToHz(13500f);

        /// <summary>
        /// The resonance (Q = 1/√2 ≈ 0.707) of a flat Butterworth low-pass with no resonant peak, matching
        /// the SF2 default initial-filter Q of 0 centibels.
        /// </summary>
        public const float ButterworthResonance = 0.70710678f;
    }
}

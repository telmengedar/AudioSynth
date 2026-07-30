namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable, rate-independent description of a DADSR modulation envelope: the delay, attack, hold,
    /// decay and release stage durations in seconds, and the sustain level as a unipolar linear value in
    /// [0, 1]. Structurally mirrors <see cref="EnvelopeParameters"/>, but drives <see cref="ModulationEnvelope"/>
    /// (SF2 gens 25-30), a sibling of the volume envelope that routes into filter cutoff (gen-11) rather
    /// than amplitude; its decay and release ramp linearly in the value domain, not geometrically.
    /// </summary>
    public readonly struct ModulationEnvelopeParameters {

        /// <summary>
        /// Creates a <see cref="ModulationEnvelopeParameters"/>.
        /// </summary>
        /// <param name="delaySeconds">time held at zero before the attack begins</param>
        /// <param name="attackSeconds">time to rise from zero to the peak level (1)</param>
        /// <param name="holdSeconds">time held at the peak before the decay begins</param>
        /// <param name="decaySeconds">time to fall linearly from the peak to <paramref name="sustainLevel"/></param>
        /// <param name="sustainLevel">unipolar level held until release, in the range [0, 1]</param>
        /// <param name="releaseSeconds">time to fall linearly from the current level to zero after note-off</param>
        public ModulationEnvelopeParameters(
            float delaySeconds,
            float attackSeconds,
            float holdSeconds,
            float decaySeconds,
            float sustainLevel,
            float releaseSeconds) {
            DelaySeconds = delaySeconds;
            AttackSeconds = attackSeconds;
            HoldSeconds = holdSeconds;
            DecaySeconds = decaySeconds;
            SustainLevel = sustainLevel;
            ReleaseSeconds = releaseSeconds;
        }

        /// <summary>Time in seconds the envelope is held at zero before the attack begins.</summary>
        public float DelaySeconds { get; }

        /// <summary>Time in seconds to rise from zero to the peak level (1).</summary>
        public float AttackSeconds { get; }

        /// <summary>Time in seconds the envelope is held at the peak before the decay begins.</summary>
        public float HoldSeconds { get; }

        /// <summary>Time in seconds to fall linearly from the peak to <see cref="SustainLevel"/>.</summary>
        public float DecaySeconds { get; }

        /// <summary>Unipolar level held until release, in the range [0, 1].</summary>
        public float SustainLevel { get; }

        /// <summary>Time in seconds to fall linearly from the current level to zero after note-off.</summary>
        public float ReleaseSeconds { get; }

        /// <summary>
        /// The SF2-specification default modulation envelope: near-instant stage times (≈0.977 ms, from
        /// −12000 timecents) and a full sustain level (1). Used wherever a region carries no mod-envelope
        /// generators; harmless on its own since a zero <see cref="FilterParameters.ModEnvToCutoffCents"/>
        /// makes the envelope's shape irrelevant to the render.
        /// </summary>
        public static ModulationEnvelopeParameters Default =>
            new ModulationEnvelopeParameters(
                Sf2DefaultTimeSeconds, Sf2DefaultTimeSeconds, Sf2DefaultTimeSeconds, Sf2DefaultTimeSeconds, 1f, Sf2DefaultTimeSeconds);

        /// <summary>
        /// Seconds corresponding to the SF2 default envelope time generator amount of −12000 timecents
        /// (2^(−12000/1200) = 2^−10 seconds ≈ 0.977 ms).
        /// </summary>
        public const float Sf2DefaultTimeSeconds = 1f / 1024f;
    }
}

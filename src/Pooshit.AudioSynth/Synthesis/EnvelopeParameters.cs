namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable, rate-independent description of a DAHDSR volume envelope: the delay, attack, hold,
    /// decay and release stage durations in seconds, and the sustain level as a linear amplitude in
    /// the range [0, 1] (1 = peak).  SF2 timecent and centibel units are converted away before an
    /// instance is built, so this type carries no SoundFont-specific knowledge.
    /// </summary>
    public readonly struct EnvelopeParameters {

        /// <summary>
        /// Creates an <see cref="EnvelopeParameters"/>.
        /// </summary>
        /// <param name="delaySeconds">time held silent before the attack begins</param>
        /// <param name="attackSeconds">time to rise from zero to the peak level</param>
        /// <param name="holdSeconds">time held at the peak before the decay begins</param>
        /// <param name="decaySeconds">time to fall from the peak to the sustain level</param>
        /// <param name="sustainLevel">linear amplitude held until release, in the range [0, 1]</param>
        /// <param name="releaseSeconds">time to fall from the current level to zero after note-off</param>
        public EnvelopeParameters(
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

        /// <summary>
        /// Time in seconds the envelope is held silent before the attack begins.
        /// </summary>
        public float DelaySeconds { get; }

        /// <summary>
        /// Time in seconds to rise from zero to the peak level.
        /// </summary>
        public float AttackSeconds { get; }

        /// <summary>
        /// Time in seconds the envelope is held at the peak before the decay begins.
        /// </summary>
        public float HoldSeconds { get; }

        /// <summary>
        /// Time in seconds to fall from the peak to the sustain level.
        /// </summary>
        public float DecaySeconds { get; }

        /// <summary>
        /// Linear amplitude held until release, in the range [0, 1] where 1 is the peak.
        /// </summary>
        public float SustainLevel { get; }

        /// <summary>
        /// Time in seconds to fall from the current level to zero after note-off.
        /// </summary>
        public float ReleaseSeconds { get; }

        /// <summary>
        /// The SF2-specification default volume envelope: near-instant delay/attack/hold/decay/release
        /// (≈0.977 ms, from −12000 timecents) and a full sustain level (0 cB of attenuation).  Used for
        /// hand-built patches and wherever an SF2 volume-envelope generator is absent.
        /// </summary>
        public static EnvelopeParameters Default =>
            new EnvelopeParameters(
                Sf2DefaultTimeSeconds,
                Sf2DefaultTimeSeconds,
                Sf2DefaultTimeSeconds,
                Sf2DefaultTimeSeconds,
                1f,
                Sf2DefaultTimeSeconds);

        /// <summary>
        /// Seconds corresponding to the SF2 default envelope time generator amount of −12000 timecents
        /// (2^(−12000/1200) = 2^−10 seconds ≈ 0.977 ms).
        /// </summary>
        public const float Sf2DefaultTimeSeconds = 1f / 1024f;
    }
}

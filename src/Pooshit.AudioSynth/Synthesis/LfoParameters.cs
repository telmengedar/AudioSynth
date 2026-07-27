namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable, rate-independent description of a per-voice modulation LFO: the delay before onset,
    /// the oscillation frequency, and the peak deviation it drives at full excursion on each of its
    /// three routed destinations (pitch, volume, filter cutoff).  SF2 timecent and absolute-cent units
    /// are converted away before an instance is built, so this type carries no SoundFont-specific
    /// knowledge.  <see cref="Default"/> is inert (all three depths zero), so building a
    /// <see cref="ModulationLfo"/> from it is an exact passthrough at any rate.
    /// </summary>
    public readonly struct LfoParameters {

        /// <summary>
        /// Creates an <see cref="LfoParameters"/>.
        /// </summary>
        /// <param name="delaySeconds">time held at zero output before the oscillation begins</param>
        /// <param name="frequencyHz">oscillation frequency in hertz</param>
        /// <param name="pitchDepthCents">peak pitch deviation, in cents, at full LFO excursion</param>
        /// <param name="volumeDepthCentibels">peak volume deviation, in centibels, at full LFO excursion (tremolo)</param>
        /// <param name="filterDepthCents">peak filter-cutoff deviation, in cents, at full LFO excursion (filter-sweep)</param>
        public LfoParameters(
            float delaySeconds,
            float frequencyHz,
            float pitchDepthCents,
            float volumeDepthCentibels,
            float filterDepthCents) {
            DelaySeconds = delaySeconds;
            FrequencyHz = frequencyHz;
            PitchDepthCents = pitchDepthCents;
            VolumeDepthCentibels = volumeDepthCentibels;
            FilterDepthCents = filterDepthCents;
        }

        /// <summary>
        /// Time in seconds the LFO output is held at zero before the oscillation begins.
        /// </summary>
        public float DelaySeconds { get; }

        /// <summary>
        /// Oscillation frequency in hertz.
        /// </summary>
        public float FrequencyHz { get; }

        /// <summary>
        /// Peak pitch deviation, in cents, applied when the LFO is at full excursion.  Zero means the
        /// LFO contributes nothing to pitch regardless of delay or frequency.
        /// </summary>
        public float PitchDepthCents { get; }

        /// <summary>
        /// Peak volume deviation, in centibels, applied when the LFO is at full excursion (tremolo).
        /// Zero means the LFO contributes nothing to volume regardless of delay or frequency.
        /// </summary>
        public float VolumeDepthCentibels { get; }

        /// <summary>
        /// Peak filter-cutoff deviation, in cents, applied when the LFO is at full excursion
        /// (filter-sweep); zero means the LFO does not affect the cutoff.
        /// </summary>
        public float FilterDepthCents { get; }

        /// <summary>
        /// The SF2-specification default modulation LFO: near-instant delay (≈0.977 ms, from −12000
        /// timecents), the default frequency (8.176 Hz, from 0 absolute cents), and zero depth on all
        /// three routed destinations.  All-zero depth makes the LFO inert regardless of delay or
        /// frequency, so hand-built patches and any region whose SF2 mod-LFO generators are absent
        /// render unaffected by the LFO.
        /// </summary>
        public static LfoParameters Default =>
            new LfoParameters(Sf2DefaultDelaySeconds, Sf2DefaultFrequencyHz, 0f, 0f, 0f);

        /// <summary>
        /// Seconds corresponding to the SF2 default LFO delay generator amount of −12000 timecents
        /// (2^(−12000/1200) = 2^−10 seconds ≈ 0.977 ms).
        /// </summary>
        public const float Sf2DefaultDelaySeconds = 1f / 1024f;

        /// <summary>
        /// Hertz corresponding to the SF2 default LFO frequency generator amount of 0 absolute cents
        /// (8.176 · 2^(0/1200) = 8.176 Hz).
        /// </summary>
        public const float Sf2DefaultFrequencyHz = 8.176f;
    }
}

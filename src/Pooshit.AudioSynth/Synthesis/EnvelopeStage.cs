namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Lifecycle stages of a DAHDSR volume envelope, traversed in order until <see cref="Finished"/>.
    /// </summary>
    public enum EnvelopeStage {

        /// <summary>Pre-attack silence: level held at zero for the delay time.</summary>
        Delay,

        /// <summary>Level rises from zero to the peak over the attack time.</summary>
        Attack,

        /// <summary>Level held at the peak for the hold time.</summary>
        Hold,

        /// <summary>Level falls from the peak to the sustain level over the decay time.</summary>
        Decay,

        /// <summary>Level held at the sustain level until the note is released.</summary>
        Sustain,

        /// <summary>Level falls from its current value to zero over the release time after note-off.</summary>
        Release,

        /// <summary>Release has reached zero; the envelope produces silence and the voice may deactivate.</summary>
        Finished
    }
}

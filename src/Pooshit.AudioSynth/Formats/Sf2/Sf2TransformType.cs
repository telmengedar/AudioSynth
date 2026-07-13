namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// SF2 section 8.2 — modulator transform operation applied to the source signal.
    /// </summary>
    public enum Sf2TransformType : ushort {

        /// <summary>No transformation; the source value is used as-is.</summary>
        Linear = 0,

        /// <summary>The absolute value of the source is used.</summary>
        AbsoluteValue = 2
    }
}

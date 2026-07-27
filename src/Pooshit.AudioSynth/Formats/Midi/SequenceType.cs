namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// How a MIDI header's division field encodes tick duration.
    /// </summary>
    public enum SequenceType {

        /// <summary>
        /// Division is pulses (ticks) per quarter note, converted to seconds via tempo meta events.
        /// The path increment 1 targets and verifies.
        /// </summary>
        Ppqn,

        /// <summary>
        /// Division is SMPTE frames-per-second and ticks-per-frame; the legacy conversion is preserved
        /// but unverified in increment 1 (DiVoid #7098 R3/O3) — parsing never crashes on it.
        /// </summary>
        Smpte
    }
}

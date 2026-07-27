namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// The high nibble of a channel voice message's status byte.
    /// </summary>
    public enum ChannelCommandType {

        /// <summary>
        /// Note-off.
        /// </summary>
        NoteOff = 0x80,

        /// <summary>
        /// Note-on (velocity 0 is a note-off in disguise; see <c>Pooshit.AudioSynth.Sequencing.MidiSequencer</c>).
        /// </summary>
        NoteOn = 0x90,

        /// <summary>
        /// Polyphonic key pressure (aftertouch).
        /// </summary>
        PolyPressure = 0xA0,

        /// <summary>
        /// Control change.
        /// </summary>
        Controller = 0xB0,

        /// <summary>
        /// Program (patch) change.
        /// </summary>
        ProgramChange = 0xC0,

        /// <summary>
        /// Channel pressure (monophonic aftertouch).
        /// </summary>
        ChannelPressure = 0xD0,

        /// <summary>
        /// Pitch wheel change.
        /// </summary>
        PitchWheel = 0xE0
    }
}

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Closed vocabulary of MIDI message categories.
    /// </summary>
    public enum MessageType {

        /// <summary>
        /// A channel voice message (note on/off, controller, program change, ...).
        /// </summary>
        Channel,

        /// <summary>
        /// A system exclusive message.
        /// </summary>
        SystemExclusive,

        /// <summary>
        /// A system common message.
        /// </summary>
        SystemCommon,

        /// <summary>
        /// A system realtime message.
        /// </summary>
        SystemRealtime,

        /// <summary>
        /// A meta message (file-level, never transmitted over a MIDI cable).
        /// </summary>
        Meta,

        /// <summary>
        /// A generic short (status + up to two data bytes) message.
        /// </summary>
        Short
    }
}

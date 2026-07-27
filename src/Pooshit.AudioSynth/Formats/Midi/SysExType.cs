namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// The two ways a system exclusive message can begin on the wire.
    /// </summary>
    public enum SysExType {

        /// <summary>
        /// The start of a new system exclusive message.
        /// </summary>
        Start = 0xF0,

        /// <summary>
        /// A continuation of a previously started system exclusive message.
        /// </summary>
        Continuation = 0xF7
    }
}

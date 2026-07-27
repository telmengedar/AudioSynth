namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// A single decoded MIDI event, independent of its position in a track.
    /// </summary>
    public interface IMidiMessage {

        /// <summary>
        /// The message's status byte.
        /// </summary>
        byte Status { get; }

        /// <summary>
        /// The message's category.
        /// </summary>
        MessageType MessageType { get; }
    }
}

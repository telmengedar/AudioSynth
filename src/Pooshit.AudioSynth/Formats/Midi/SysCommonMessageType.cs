namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Closed vocabulary of system common status bytes (0xF1-0xF6).
    /// </summary>
    public enum SysCommonMessageType {

        /// <summary>MIDI time code quarter frame.</summary>
        MidiTimeCode = 0xF1,

        /// <summary>Song position pointer.</summary>
        SongPositionPointer,

        /// <summary>Song select.</summary>
        SongSelect,

        /// <summary>Tune request.</summary>
        TuneRequest = 0xF6
    }
}

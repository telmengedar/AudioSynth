namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Closed vocabulary of MIDI file meta-event types (never transmitted over a MIDI cable).
    /// </summary>
    public enum MetaMessageType {

        /// <summary>Sequence number.</summary>
        SequenceNumber,

        /// <summary>Free text.</summary>
        Text,

        /// <summary>Copyright notice.</summary>
        Copyright,

        /// <summary>Track name.</summary>
        TrackName,

        /// <summary>Instrument name.</summary>
        InstrumentName,

        /// <summary>Lyric.</summary>
        Lyric,

        /// <summary>Marker.</summary>
        Marker,

        /// <summary>Cue point.</summary>
        CuePoint,

        /// <summary>Program (patch) name.</summary>
        ProgramName,

        /// <summary>MIDI device name.</summary>
        DeviceName,

        /// <summary>End of track.</summary>
        EndOfTrack = 0x2F,

        /// <summary>Set tempo (microseconds per quarter note).</summary>
        Tempo = 0x51,

        /// <summary>SMPTE offset.</summary>
        SmpteOffset = 0x54,

        /// <summary>Time signature.</summary>
        TimeSignature = 0x58,

        /// <summary>Key signature.</summary>
        KeySignature,

        /// <summary>Sequencer-specific proprietary event.</summary>
        ProprietaryEvent = 0x7F
    }
}

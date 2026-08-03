namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// Effect commands carried by a <see cref="Cell"/>. Byte-backed and open: unnamed values are legal and
    /// pass through the importer uninterpreted.
    /// </summary>
    public enum TrackerEffectCommand : byte {

        /// <summary>No effect (the default, empty sub-column).</summary>
        None = 0,

        /// <summary>Set the playback speed in ticks per row; parameter is the tick count.</summary>
        SetSpeed = 1,

        /// <summary>Set the playback tempo in BPM; parameter is the beats-per-minute value.</summary>
        SetTempo = 2,

        /// <summary>Jump the cursor to an order-list position; parameter is the target index into <see cref="Song.Order"/>.</summary>
        JumpToOrder = 3,

        /// <summary>Per-tick volume ramp; hi nibble = up/tick, lo nibble = down/tick (hi>0 selects up).</summary>
        VolumeSlide = 4,

        /// <summary>Per-tick upward pitch slide; parameter is the semitone step per tick (engine scale const).</summary>
        PortamentoUp = 5,

        /// <summary>Per-tick downward pitch slide; parameter is the semitone step per tick (engine scale const).</summary>
        PortamentoDown = 6,

        /// <summary>Slides toward the cell's note without retriggering it; parameter is the per-tick semitone step.</summary>
        TonePortamento = 7,

        /// <summary>Cycles the pitch through base/hi-nibble/lo-nibble semitone offsets every tick.</summary>
        Arpeggio = 8,

        /// <summary>Per-tick sinusoidal pitch modulation; hi nibble = rate, lo nibble = depth.</summary>
        Vibrato = 9,

        /// <summary>Re-triggers the sounding note every parameter ticks.</summary>
        Retrigger = 10,

        /// <summary>Silences the channel at the tick given by the parameter.</summary>
        NoteCut = 11,

        /// <summary>Withholds the whole cell's controls and note until the tick given by the parameter.</summary>
        NoteDelay = 12,

        /// <summary>Sets the channel's pan at row-enter; parameter is the pan on the 0..128 scale (see <see cref="TrackerPan"/>).</summary>
        SetPan = 13
    }
}

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
        JumpToOrder = 3
    }
}

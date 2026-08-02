namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// One grid position: the five tracker sub-columns as plain value data. The all-zero <c>default</c> is
    /// a fully empty cell (every optional sub-column uses <c>0</c> = absent).
    /// </summary>
    public struct Cell {

        /// <summary>
        /// Note sub-column: <c>0</c> empty, <c>1..120</c> a playable note (MIDI key = value − 1),
        /// <c>254</c> note-off, <c>255</c> note-cut. See <see cref="TrackerNotes"/>.
        /// </summary>
        public byte Note { get; set; }

        /// <summary>
        /// Instrument sub-column: <c>0</c> = none (reuse the channel's current instrument), otherwise a
        /// 1-based slot into <see cref="Song.Instruments"/> (slot <c>n</c> → <c>Instruments[n − 1]</c>).
        /// </summary>
        public byte Instrument { get; set; }

        /// <summary>
        /// Volume sub-column: <c>0</c> = not set, <c>1..64</c> = an explicit level (channel gain =
        /// value / 64). Silence is expressed via note-cut, so <c>0</c> is free to mean "absent".
        /// </summary>
        public byte Volume { get; set; }

        /// <summary>
        /// Effect command sub-column. Unknown/unnamed values are legal and pass through the importer
        /// uninterpreted.
        /// </summary>
        public TrackerEffectCommand Effect { get; set; }

        /// <summary>Effect parameter sub-column; command-specific.</summary>
        public byte EffectParam { get; set; }
    }
}

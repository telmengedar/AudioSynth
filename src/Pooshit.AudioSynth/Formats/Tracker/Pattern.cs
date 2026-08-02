using System;

namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// A flat, row-major <see cref="Cell"/> grid (index = <c>row × Song.ChannelCount + channel</c>) of
    /// the pattern's effective row count.
    /// </summary>
    public class Pattern {

        /// <summary>
        /// This pattern's row count; <c>null</c> = not overridden, use <c>Song.DefaultRows</c>.
        /// </summary>
        public int? Rows { get; set; }

        /// <summary>
        /// Row-major cell grid; length is the effective row count (<see cref="Rows"/> ?? <c>Song.DefaultRows</c>) × <c>Song.ChannelCount</c>.
        /// </summary>
        public Cell[] Cells { get; set; } = Array.Empty<Cell>();
    }
}

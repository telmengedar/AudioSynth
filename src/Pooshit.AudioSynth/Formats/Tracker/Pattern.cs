using System;

namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// A flat, row-major <see cref="Cell"/> grid (index = <c>row × Song.ChannelCount + channel</c>) of
    /// height <see cref="Rows"/>.
    /// </summary>
    public class Pattern {

        /// <summary>Number of rows in this pattern; authoritative for playback (may differ per pattern).</summary>
        public int Rows { get; set; }

        /// <summary>
        /// Row-major cell grid; length is <see cref="Rows"/> × the owning <c>Song.ChannelCount</c>.
        /// </summary>
        public Cell[] Cells { get; set; } = Array.Empty<Cell>();
    }
}

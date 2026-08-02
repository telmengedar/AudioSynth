using System;

namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// One pattern: a <see cref="Rows"/> × <c>Song.ChannelCount</c> grid of <see cref="Cell"/>s stored as
    /// a flat, row-major array (index = <c>row × channelCount + channel</c>). Flat rather than
    /// multidimensional or jagged so it serializes to a plain JSON array on any engine serializer.
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

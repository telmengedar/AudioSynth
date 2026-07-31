using System.Collections.Generic;

namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// Immutable, offset-sorted view of a <see cref="Sequencing.Timeline.Timeline"/>, produced by
    /// <see cref="Sequencing.Timeline.Timeline.Compile"/>; the driver's hot-path dispatch source.
    /// </summary>
    public sealed class CompiledSchedule {

        readonly TimelineEntry[] entries;

        internal CompiledSchedule(TimelineEntry[] entries) {
            this.entries = entries;
        }

        /// <summary>Every entry, ordered non-decreasing by <see cref="TimelineEntry.SampleOffset"/>.</summary>
        public IReadOnlyList<TimelineEntry> Entries => entries;

        /// <summary>Number of entries.</summary>
        public int Count => entries.Length;

        /// <summary>
        /// Binary-searches for the index of the first entry whose offset is at or after
        /// <paramref name="sampleOffset"/>; <see cref="Count"/> if none.
        /// </summary>
        public int FindFirstAtOrAfter(long sampleOffset) {
            int low = 0;
            int high = entries.Length;
            while (low < high) {
                int mid = low + (high - low) / 2;
                if (entries[mid].SampleOffset < sampleOffset)
                    low = mid + 1;
                else
                    high = mid;
            }
            return low;
        }
    }
}

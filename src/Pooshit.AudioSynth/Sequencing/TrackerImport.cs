namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Result of <see cref="TrackerTimelineImporter.Import"/>: the lowered timeline plus an optional loop region.
    /// </summary>
    public readonly struct TrackerImport {

        /// <summary>Creates a <see cref="TrackerImport"/>; <paramref name="loopStart"/> and <paramref name="loopEnd"/> must both be set or both be null.</summary>
        public TrackerImport(Timeline.Timeline timeline, long? loopStart, long? loopEnd) {
            Timeline = timeline;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
        }

        /// <summary>The lowered, uncompiled timeline.</summary>
        public Timeline.Timeline Timeline { get; }

        /// <summary>Inclusive loop-region start in samples; <c>null</c> means finite playback.</summary>
        public long? LoopStart { get; }

        /// <summary>Exclusive loop-region end in samples; <c>null</c> means finite playback.</summary>
        public long? LoopEnd { get; }
    }
}

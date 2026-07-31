namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// One <see cref="Sequencing.Timeline.NeutralEvent"/> placed at an absolute sample offset inside a
    /// <see cref="Sequencing.Timeline.Timeline"/>; the driver's compiled, read-only dispatch unit.
    /// </summary>
    public sealed class TimelineEntry {

        internal TimelineEntry(long eventId, long sampleOffset, NeutralEvent @event, int? gateId) {
            EventId = eventId;
            SampleOffset = sampleOffset;
            Event = @event;
            GateId = gateId;
        }

        /// <summary>Stable identity, unique for the lifetime of the owning <see cref="Sequencing.Timeline.Timeline"/>.</summary>
        public long EventId { get; }

        /// <summary>Absolute sample offset from the start of the timeline.</summary>
        public long SampleOffset { get; }

        /// <summary>The neutral synth-control operation to dispatch.</summary>
        public NeutralEvent Event { get; }

        /// <summary>
        /// Optional rhythm-game gate group id (Phase 3 seam); <c>null</c> means unconditional (always
        /// dispatched).
        /// </summary>
        public int? GateId { get; }
    }
}

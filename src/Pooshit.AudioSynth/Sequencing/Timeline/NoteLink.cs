namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// Associates a <c>NoteOn</c> entry with its matching <c>NoteOff</c> entry so an editor/gate can treat
    /// a note as one unit, while the timeline still dispatches the two ends separately.
    /// </summary>
    public sealed class NoteLink {

        internal NoteLink(long noteId, long onEventId, long offEventId) {
            NoteId = noteId;
            OnEventId = onEventId;
            OffEventId = offEventId;
        }

        /// <summary>Stable identity for the linked note as a unit.</summary>
        public long NoteId { get; }

        /// <summary>The <see cref="TimelineEntry.EventId"/> of the <c>NoteOn</c> half.</summary>
        public long OnEventId { get; }

        /// <summary>The <see cref="TimelineEntry.EventId"/> of the <c>NoteOff</c> half.</summary>
        public long OffEventId { get; }
    }
}

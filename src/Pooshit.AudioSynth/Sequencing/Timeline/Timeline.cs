using System;
using System.Collections.Generic;

namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// The editable, in-memory core: a mutable, MIDI-neutral sequence of <see cref="NeutralEvent"/>s
    /// addressed by stable event id. Populated by an importer (e.g. MIDI) or an editor, and
    /// <see cref="Compile"/>d into an immutable <see cref="CompiledSchedule"/> for the real-time driver.
    /// </summary>
    public sealed class Timeline {

        sealed class MutableEntry {
            internal long EventId;
            internal long SampleOffset;
            internal NeutralEvent Event;
            internal int? GateId;
            internal long InsertionOrder;
        }

        readonly List<MutableEntry> entries = new List<MutableEntry>();
        readonly Dictionary<long, MutableEntry> byId = new Dictionary<long, MutableEntry>();
        readonly Dictionary<long, NoteLink> noteLinks = new Dictionary<long, NoteLink>();
        long nextEventId = 1;
        long nextNoteId = 1;
        long nextInsertionOrder;

        /// <summary>
        /// Adds a new entry at <paramref name="sampleOffset"/>; ties at the same offset dispatch in
        /// insertion order.
        /// </summary>
        /// <returns>the new entry's stable event id</returns>
        public long Add(long sampleOffset, NeutralEvent @event, int? gateId = null) {
            MutableEntry entry = new MutableEntry {
                EventId = nextEventId++,
                SampleOffset = sampleOffset,
                Event = @event,
                GateId = gateId,
                InsertionOrder = nextInsertionOrder++
            };
            entries.Add(entry);
            byId.Add(entry.EventId, entry);
            return entry.EventId;
        }

        /// <summary>Removes the entry identified by <paramref name="eventId"/>; a no-op if absent.</summary>
        public void Remove(long eventId) {
            if (!byId.TryGetValue(eventId, out MutableEntry? entry))
                return;
            entries.Remove(entry);
            byId.Remove(eventId);
        }

        /// <summary>Repositions an existing entry to <paramref name="newSampleOffset"/>.</summary>
        public void Move(long eventId, long newSampleOffset) {
            if (byId.TryGetValue(eventId, out MutableEntry? entry))
                entry.SampleOffset = newSampleOffset;
        }

        /// <summary>Replaces an existing entry's <see cref="NeutralEvent"/> payload.</summary>
        public void Modify(long eventId, NeutralEvent newEvent) {
            if (byId.TryGetValue(eventId, out MutableEntry? entry))
                entry.Event = newEvent;
        }

        /// <summary>
        /// Associates a <c>NoteOn</c> entry with its matching <c>NoteOff</c> entry so an editor/gate can
        /// address the note as one unit.
        /// </summary>
        /// <returns>the new note link's stable note id</returns>
        public long LinkNote(long onEventId, long offEventId) {
            long noteId = nextNoteId++;
            noteLinks.Add(noteId, new NoteLink(noteId, onEventId, offEventId));
            return noteId;
        }

        /// <summary>Every linked note, keyed by <see cref="NoteLink.NoteId"/>.</summary>
        public IReadOnlyDictionary<long, NoteLink> NoteLinks => noteLinks;

        /// <summary>
        /// Produces an immutable <see cref="CompiledSchedule"/>: every entry sorted by
        /// <c>(SampleOffset, insertion order)</c>, the stable ordering the driver relies on for
        /// simultaneous-event and RAC-batch parity.
        /// </summary>
        public CompiledSchedule Compile() {
            MutableEntry[] sorted = entries.ToArray();
            Array.Sort(sorted, (a, b) => {
                int byOffset = a.SampleOffset.CompareTo(b.SampleOffset);
                return byOffset != 0 ? byOffset : a.InsertionOrder.CompareTo(b.InsertionOrder);
            });

            TimelineEntry[] compiled = new TimelineEntry[sorted.Length];
            for (int i = 0; i < sorted.Length; i++)
                compiled[i] = new TimelineEntry(sorted[i].EventId, sorted[i].SampleOffset, sorted[i].Event, sorted[i].GateId);
            return new CompiledSchedule(compiled);
        }
    }
}

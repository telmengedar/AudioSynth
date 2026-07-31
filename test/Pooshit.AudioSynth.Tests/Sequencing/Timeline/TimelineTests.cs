using NUnit.Framework;
using Pooshit.AudioSynth.Sequencing.Timeline;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="Timeline"/> mutation and compile-ordering tests: stable ordering on ties, the mutation
    /// seams (Add/Remove/Move/Modify/LinkNote), and <see cref="CompiledSchedule.FindFirstAtOrAfter"/>.
    /// </summary>
    [TestFixture]
    public class TimelineTests {

        [Test]
        [Description("Entries at the same sample offset must compile in insertion order (RAC-batch/simultaneous-event parity).")]
        public void Compile_EntriesAtSameOffset_PreserveInsertionOrder() {
            Timeline timeline = new Timeline();
            timeline.Add(0, NeutralEvent.SetGain(0, 0.1f));
            timeline.Add(0, NeutralEvent.SetPan(0, 0.2f));
            timeline.Add(0, NeutralEvent.SetGain(1, 0.3f));

            CompiledSchedule schedule = timeline.Compile();

            Assert.That(schedule.Count, Is.EqualTo(3));
            Assert.That(schedule.Entries[0].Event.Kind, Is.EqualTo(NeutralEventKind.SetGain));
            Assert.That(schedule.Entries[0].Event.Channel, Is.EqualTo(0));
            Assert.That(schedule.Entries[1].Event.Kind, Is.EqualTo(NeutralEventKind.SetPan));
            Assert.That(schedule.Entries[2].Event.Kind, Is.EqualTo(NeutralEventKind.SetGain));
            Assert.That(schedule.Entries[2].Event.Channel, Is.EqualTo(1));
        }

        [Test]
        [Description("Entries must compile sorted by sample offset regardless of insertion order.")]
        public void Compile_EntriesAddedOutOfOrder_SortByOffset() {
            Timeline timeline = new Timeline();
            long third = timeline.Add(300, NeutralEvent.NoteOn(0, 60, 100));
            long first = timeline.Add(100, NeutralEvent.NoteOn(0, 61, 100));
            long second = timeline.Add(200, NeutralEvent.NoteOn(0, 62, 100));

            CompiledSchedule schedule = timeline.Compile();

            Assert.That(schedule.Entries[0].EventId, Is.EqualTo(first));
            Assert.That(schedule.Entries[1].EventId, Is.EqualTo(second));
            Assert.That(schedule.Entries[2].EventId, Is.EqualTo(third));
        }

        [Test]
        [Description("Remove must drop the entry from the compiled schedule; a removal of an unknown id is a no-op.")]
        public void Remove_ExistingEntry_DropsItFromCompile() {
            Timeline timeline = new Timeline();
            long keep = timeline.Add(0, NeutralEvent.NoteOn(0, 60, 100));
            long drop = timeline.Add(10, NeutralEvent.NoteOff(0, 60));

            timeline.Remove(drop);
            timeline.Remove(999);

            CompiledSchedule schedule = timeline.Compile();
            Assert.That(schedule.Count, Is.EqualTo(1));
            Assert.That(schedule.Entries[0].EventId, Is.EqualTo(keep));
        }

        [Test]
        [Description("Move must reposition an entry's sample offset for the next compile.")]
        public void Move_ExistingEntry_RepositionsForNextCompile() {
            Timeline timeline = new Timeline();
            long id = timeline.Add(100, NeutralEvent.NoteOn(0, 60, 100));

            timeline.Move(id, 500);

            CompiledSchedule schedule = timeline.Compile();
            Assert.That(schedule.Entries[0].SampleOffset, Is.EqualTo(500));
        }

        [Test]
        [Description("Modify must replace an entry's neutral event payload for the next compile.")]
        public void Modify_ExistingEntry_ReplacesEventForNextCompile() {
            Timeline timeline = new Timeline();
            long id = timeline.Add(0, NeutralEvent.SetGain(0, 0.5f));

            timeline.Modify(id, NeutralEvent.SetGain(0, 0.9f));

            CompiledSchedule schedule = timeline.Compile();
            Assert.That(schedule.Entries[0].Event.Value, Is.EqualTo(0.9f));
        }

        [Test]
        [Description("LinkNote must associate the NoteOn/NoteOff pair, addressable as one unit via NoteLinks.")]
        public void LinkNote_OnAndOffEntry_IsAddressableAsOneUnit() {
            Timeline timeline = new Timeline();
            long onId = timeline.Add(0, NeutralEvent.NoteOn(0, 60, 100));
            long offId = timeline.Add(500, NeutralEvent.NoteOff(0, 60));

            long noteId = timeline.LinkNote(onId, offId);

            Assert.That(timeline.NoteLinks[noteId].OnEventId, Is.EqualTo(onId));
            Assert.That(timeline.NoteLinks[noteId].OffEventId, Is.EqualTo(offId));
        }

        [Test]
        [Description("FindFirstAtOrAfter must binary-search to the first entry at or after the given offset.")]
        public void FindFirstAtOrAfter_MidOffset_ReturnsFirstMatchingIndex() {
            Timeline timeline = new Timeline();
            timeline.Add(0, NeutralEvent.NoteOn(0, 60, 100));
            timeline.Add(100, NeutralEvent.NoteOn(0, 61, 100));
            timeline.Add(100, NeutralEvent.NoteOn(0, 62, 100));
            timeline.Add(200, NeutralEvent.NoteOn(0, 63, 100));
            CompiledSchedule schedule = timeline.Compile();

            Assert.That(schedule.FindFirstAtOrAfter(50), Is.EqualTo(1));
            Assert.That(schedule.FindFirstAtOrAfter(100), Is.EqualTo(1));
            Assert.That(schedule.FindFirstAtOrAfter(150), Is.EqualTo(3));
            Assert.That(schedule.FindFirstAtOrAfter(1000), Is.EqualTo(4));
        }
    }
}

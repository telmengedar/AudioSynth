using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="MidiTimelineImporter"/> fidelity tests: the imported <see cref="CompiledSchedule"/> must
    /// reproduce the pre-refactor <c>ApplyMessage</c> GM-decode behavior exactly — bank/RPN folded into
    /// resolved events, never raw timeline entries.
    /// </summary>
    [TestFixture]
    public class MidiTimelineImporterTests {

        const int SampleRate = 44100;
        const int PercussionChannel = 9;

        static TimedMessageSequence BuildSequence(params MidiTrackEventBuilder[] builders) {
            byte[][] chunks = new byte[builders.Length][];
            for (int i = 0; i < builders.Length; i++)
                chunks[i] = builders[i].EndOfTrack().BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, chunks)));
            return new TimedMessageSequence(file);
        }

        [Test]
        [Description("An empty sequence still imports the 80 GM-reset events (5 kinds x 16 channels) at " +
                     "offset 0, per-channel in patch/gain/pan/reverb/chorus order.")]
        public void Import_EmptySequence_EmitsGmResetBatchInPerChannelOrder() {
            CompiledSchedule schedule = MidiTimelineImporter.Import(BuildSequence(new MidiTrackEventBuilder()), SampleRate).Compile();

            Assert.That(schedule.Count, Is.EqualTo(80));
            for (int channel = 0; channel < 16; channel++) {
                int baseIndex = channel * 5;
                Assert.That(schedule.Entries[baseIndex].Event.Kind, Is.EqualTo(NeutralEventKind.SetPatch));
                Assert.That(schedule.Entries[baseIndex + 1].Event.Kind, Is.EqualTo(NeutralEventKind.SetGain));
                Assert.That(schedule.Entries[baseIndex + 2].Event.Kind, Is.EqualTo(NeutralEventKind.SetPan));
                Assert.That(schedule.Entries[baseIndex + 3].Event.Kind, Is.EqualTo(NeutralEventKind.SetReverbSend));
                Assert.That(schedule.Entries[baseIndex + 4].Event.Kind, Is.EqualTo(NeutralEventKind.SetChorusSend));
                Assert.That(schedule.Entries[baseIndex].Event.Channel, Is.EqualTo(channel));
                Assert.That(schedule.Entries[baseIndex].SampleOffset, Is.EqualTo(0));
            }
            Assert.That(schedule.Entries[0].Event.Bank, Is.EqualTo(0));
            Assert.That(schedule.Entries[PercussionChannel * 5].Event.Bank, Is.EqualTo(128),
                "the percussion channel must GM-reset into bank 128.");
        }

        [Test]
        [Description("CC0/CC32 alone (no ProgramChange) must fold into decode state only -- no extra timeline event beyond the 80 GM-reset entries.")]
        public void Import_BankSelectAlone_ProducesNoExtraEvent() {
            CompiledSchedule schedule = MidiTimelineImporter.Import(
                BuildSequence(new MidiTrackEventBuilder().Controller(0, 0, 0, 8).Controller(0, 0, 32, 3)), SampleRate).Compile();

            Assert.That(schedule.Count, Is.EqualTo(80));
        }

        [Test]
        [Description("CC0=8 then ProgramChange(5) must fold into one resolved SetPatch(bank:8, program:5) event.")]
        public void Import_BankSelectThenProgramChange_FoldsIntoResolvedSetPatch() {
            CompiledSchedule schedule = MidiTimelineImporter.Import(
                BuildSequence(new MidiTrackEventBuilder().Controller(0, 0, 0, 8).ProgramChange(0, 0, 5)), SampleRate).Compile();

            TimelineEntry programChange = schedule.Entries[80];
            Assert.That(programChange.Event.Kind, Is.EqualTo(NeutralEventKind.SetPatch));
            Assert.That(programChange.Event.Bank, Is.EqualTo(8));
            Assert.That(programChange.Event.Program, Is.EqualTo(5));
        }

        [Test]
        [Description("A ProgramChange on the percussion channel always resolves bank 128, ignoring any latched bank-select.")]
        public void Import_ProgramChangeOnPercussionChannel_AlwaysResolvesBank128() {
            CompiledSchedule schedule = MidiTimelineImporter.Import(
                BuildSequence(new MidiTrackEventBuilder().Controller(0, PercussionChannel, 0, 8).ProgramChange(0, PercussionChannel, 57)),
                SampleRate).Compile();

            TimelineEntry programChange = schedule.Entries[80];
            Assert.That(programChange.Event.Bank, Is.EqualTo(128));
            Assert.That(programChange.Event.Program, Is.EqualTo(57));
        }

        [Test]
        [Description("RPN 0 (CC101=0, CC100=0) plus Data Entry (CC6) must fold into the PitchWheel's resolved " +
                     "SetPitchBend semitone value -- no raw RPN/DataEntry timeline event.")]
        public void Import_Rpn0PlusDataEntry_FoldsIntoResolvedPitchBend() {
            CompiledSchedule schedule = MidiTimelineImporter.Import(
                BuildSequence(new MidiTrackEventBuilder()
                    .Controller(0, 3, 101, 0)
                    .Controller(0, 3, 100, 0)
                    .Controller(0, 3, 6, 12)
                    .PitchWheel(0, 3, 16383)),
                SampleRate).Compile();

            Assert.That(schedule.Count, Is.EqualTo(81), "only the resolved SetPitchBend is added -- RPN/DataEntry produce no event.");
            TimelineEntry bend = schedule.Entries[80];
            Assert.That(bend.Event.Kind, Is.EqualTo(NeutralEventKind.SetPitchBend));
            Assert.That(bend.Event.Value, Is.EqualTo(12f).Within(0.01f));
        }

        [Test]
        [Description("CC121 (Reset All Controllers) must emit exactly the strict GM-RAC batch in order: " +
                     "modulation, gain (expression reset), sustain-off, pitch-bend-center -- never pan/patch/reverb/chorus.")]
        public void Import_ResetAllControllers_EmitsStrictBatchInOrder() {
            CompiledSchedule schedule = MidiTimelineImporter.Import(
                BuildSequence(new MidiTrackEventBuilder().Controller(0, 4, 121, 0)), SampleRate).Compile();

            Assert.That(schedule.Count, Is.EqualTo(84));
            Assert.That(schedule.Entries[80].Event.Kind, Is.EqualTo(NeutralEventKind.SetModulation));
            Assert.That(schedule.Entries[81].Event.Kind, Is.EqualTo(NeutralEventKind.SetGain));
            Assert.That(schedule.Entries[82].Event.Kind, Is.EqualTo(NeutralEventKind.SetSustain));
            Assert.That(schedule.Entries[82].Event.Held, Is.False);
            Assert.That(schedule.Entries[83].Event.Kind, Is.EqualTo(NeutralEventKind.SetPitchBend));
            Assert.That(schedule.Entries[83].Event.Value, Is.EqualTo(0f));
        }

        [Test]
        [Description("A NoteOn followed by its NoteOff must be linked as one note, addressable via Timeline.NoteLinks.")]
        public void Import_NoteOnThenNoteOff_LinksAsOneNote() {
            Timeline timeline = MidiTimelineImporter.Import(
                BuildSequence(new MidiTrackEventBuilder().NoteOn(0, 2, 60, 100).NoteOff(240, 2, 60)), SampleRate);

            Assert.That(timeline.NoteLinks, Has.Count.EqualTo(1));
            foreach (NoteLink link in timeline.NoteLinks.Values) {
                CompiledSchedule schedule = timeline.Compile();
                TimelineEntry onEntry = FindEntry(schedule, link.OnEventId);
                TimelineEntry offEntry = FindEntry(schedule, link.OffEventId);
                Assert.That(onEntry.Event.Kind, Is.EqualTo(NeutralEventKind.NoteOn));
                Assert.That(offEntry.Event.Kind, Is.EqualTo(NeutralEventKind.NoteOff));
                Assert.That(offEntry.SampleOffset, Is.GreaterThan(onEntry.SampleOffset));
            }
        }

        static TimelineEntry FindEntry(CompiledSchedule schedule, long eventId) {
            foreach (TimelineEntry entry in schedule.Entries) {
                if (entry.EventId == eventId)
                    return entry;
            }
            Assert.Fail($"No entry found for event id {eventId}.");
            return null!;
        }
    }
}

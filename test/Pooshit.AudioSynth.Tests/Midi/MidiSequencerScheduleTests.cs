using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deterministic <see cref="MidiSequencer.BuildSchedule"/> tests: exact sample offsets (criterion 2)
    /// and NoteOn-velocity-0 folding to NoteOff (R2). Pure — no audio is rendered.
    /// </summary>
    [TestFixture]
    public class MidiSequencerScheduleTests {

        const int SampleRate = 44100;

        [Test]
        [Description("Success criterion 2: a known PPQN division and constant 120 BPM tempo place "
                   + "NoteOn/NoteOff at the exact expected sample offsets.")]
        public void BuildSchedule_KnownTempoAndDivision_PlacesEventsAtExpectedSampleOffsets() {
            byte[] track = new MidiTrackEventBuilder()
                .Tempo(0, 500000)
                .NoteOn(0, 0, 60, 100)
                .NoteOff(480, 0, 60)
                .EndOfTrack()
                .BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, track)));
            TimedMessageSequence sequence = new TimedMessageSequence(file);

            ScheduledMidiEvent[] schedule = MidiSequencer.BuildSchedule(sequence, SampleRate);

            Assert.That(schedule, Has.Length.EqualTo(4));
            Assert.That(schedule[0].SampleOffset, Is.EqualTo(0), "Tempo meta lands at sample 0.");
            Assert.That(schedule[1].SampleOffset, Is.EqualTo(0), "NoteOn at tick 0 lands at sample 0.");
            Assert.That(schedule[2].SampleOffset, Is.EqualTo(22050),
                "480 ticks at 480 PPQN / 120 BPM is exactly one quarter note (0.5s) = 22050 samples at 44100Hz.");
            Assert.That(schedule[3].SampleOffset, Is.EqualTo(22050), "EndOfTrack at the same tick lands at the same sample.");
        }

        [Test]
        [Description("BuildSchedule offsets are monotonically non-decreasing, so chords (zero-gap events) apply correctly.")]
        public void BuildSchedule_Always_ProducesNonDecreasingOffsets() {
            byte[] track = new MidiTrackEventBuilder()
                .Tempo(0, 500000)
                .NoteOn(0, 0, 60, 100)
                .NoteOn(0, 0, 64, 100)
                .NoteOff(240, 0, 60)
                .NoteOff(0, 0, 64)
                .EndOfTrack()
                .BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, track)));
            TimedMessageSequence sequence = new TimedMessageSequence(file);

            ScheduledMidiEvent[] schedule = MidiSequencer.BuildSchedule(sequence, SampleRate);

            for (int i = 1; i < schedule.Length; i++)
                Assert.That(schedule[i].SampleOffset, Is.GreaterThanOrEqualTo(schedule[i - 1].SampleOffset));
        }

        [Test]
        [Description("R2 / DiVoid #7098: a NoteOn with velocity 0 must fold into a NoteOff-equivalent "
                   + "scheduled event, or the note would never release.")]
        public void BuildSchedule_NoteOnVelocityZero_FoldsToNoteOff() {
            byte[] track = new MidiTrackEventBuilder()
                .NoteOn(0, 2, 67, 100)
                .NoteOn(120, 2, 67, 0)
                .EndOfTrack()
                .BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, track)));
            TimedMessageSequence sequence = new TimedMessageSequence(file);

            ScheduledMidiEvent[] schedule = MidiSequencer.BuildSchedule(sequence, SampleRate);

            ChannelMessage folded = (ChannelMessage)schedule[1].Message;
            Assert.That(folded.Command, Is.EqualTo(ChannelCommandType.NoteOff),
                "A velocity-0 NoteOn must be scheduled as a NoteOff-equivalent.");
            Assert.That(folded.MidiChannel, Is.EqualTo(2));
            Assert.That(folded.Data1, Is.EqualTo(67));
        }
    }
}

using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Midi;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Parser round-trip and defensive-parse tests for <see cref="MidiFile"/>, mirroring
    /// <c>Sf2LoaderTests</c>'s untrusted-input coverage for the SF2 loader.
    /// </summary>
    [TestFixture]
    public class MidiFileParserTests {

        [Test]
        [Description("A minimal one-track PPQN file round-trips: header fields and every message's "
                   + "type/position are preserved in order.")]
        public void Read_KnownEvents_ParsesHeaderAndMessagesInOrder() {
            byte[] track = new MidiTrackEventBuilder()
                .Tempo(0, 500000)
                .NoteOn(0, 0, 60, 100)
                .NoteOff(240, 0, 60)
                .EndOfTrack()
                .BuildChunk();
            byte[] file = MidiTestBuilder.BuildFile(480, track);

            MidiFile parsed = MidiFile.Read(new MemoryStream(file));

            Assert.That(parsed.Header.Format, Is.EqualTo(1));
            Assert.That(parsed.Header.TrackCount, Is.EqualTo(1));
            Assert.That(parsed.Header.Division, Is.EqualTo(480));
            Assert.That(parsed.Header.SequenceType, Is.EqualTo(SequenceType.Ppqn));

            Assert.That(parsed.Tracks, Has.Length.EqualTo(1));
            TrackMessage[] messages = parsed.Tracks[0].Messages;
            Assert.That(messages, Has.Length.EqualTo(4));

            Assert.That(messages[0].Position, Is.EqualTo(0));
            Assert.That(messages[0].Message, Is.InstanceOf<MetaMessage>());
            Assert.That(((MetaMessage)messages[0].Message).MetaType, Is.EqualTo(MetaMessageType.Tempo));

            Assert.That(messages[1].Position, Is.EqualTo(0));
            ChannelMessage noteOn = (ChannelMessage)messages[1].Message;
            Assert.That(noteOn.Command, Is.EqualTo(ChannelCommandType.NoteOn));
            Assert.That(noteOn.MidiChannel, Is.EqualTo(0));
            Assert.That(noteOn.Data1, Is.EqualTo(60));
            Assert.That(noteOn.Data2, Is.EqualTo(100));

            Assert.That(messages[2].Position, Is.EqualTo(240));
            ChannelMessage noteOff = (ChannelMessage)messages[2].Message;
            Assert.That(noteOff.Command, Is.EqualTo(ChannelCommandType.NoteOff));
            Assert.That(noteOff.Data1, Is.EqualTo(60));

            Assert.That(messages[3].Position, Is.EqualTo(240));
            Assert.That(((MetaMessage)messages[3].Message).MetaType, Is.EqualTo(MetaMessageType.EndOfTrack));
        }

        [Test]
        [Description("Running status: a second channel event of the same status omits the status byte "
                   + "and must still decode with the correct command, channel, and data.")]
        public void Read_RunningStatus_SecondNoteOmitsStatusByte() {
            byte[] body = Concat(
                MidiTestBuilder.EncodeVariableLength(0), new byte[] { 0x90, 60, 100 },
                MidiTestBuilder.EncodeVariableLength(120), new byte[] { 64, 90 },
                MidiTestBuilder.EncodeVariableLength(0), new byte[] { 0xFF, 0x2F, 0x00 });
            byte[] file = MidiTestBuilder.BuildFile(480, MidiTestBuilder.BuildTrackChunk(body));

            MidiFile parsed = MidiFile.Read(new MemoryStream(file));

            TrackMessage[] messages = parsed.Tracks[0].Messages;
            Assert.That(messages, Has.Length.EqualTo(3));

            ChannelMessage second = (ChannelMessage)messages[1].Message;
            Assert.That(second.Command, Is.EqualTo(ChannelCommandType.NoteOn));
            Assert.That(second.MidiChannel, Is.EqualTo(0));
            Assert.That(second.Data1, Is.EqualTo(64));
            Assert.That(second.Data2, Is.EqualTo(90));
            Assert.That(messages[1].Position, Is.EqualTo(120));
        }

        [Test]
        [Description("A stream not starting with the 'MThd' tag must throw InvalidMidiFileException, "
                   + "not silently misparse.")]
        public void Read_WrongHeaderTag_ThrowsInvalidMidiFileException() {
            byte[] garbage = { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 0, 0, 0, 6, 0, 1, 0, 1, 1, 224 };

            Assert.Throws<InvalidMidiFileException>(
                () => MidiFile.Read(new MemoryStream(garbage)),
                "A non-'MThd' stream must be rejected with InvalidMidiFileException.");
        }

        [Test]
        [Description("A file truncated mid-header must throw InvalidMidiFileException, not a raw "
                   + "EndOfStreamException.")]
        public void Read_TruncatedHeader_ThrowsInvalidMidiFileException() {
            byte[] file = MidiTestBuilder.BuildHeaderChunk(1, 1, 480);
            byte[] truncated = new byte[file.Length - 4];
            System.Array.Copy(file, truncated, truncated.Length);

            Assert.Throws<InvalidMidiFileException>(
                () => MidiFile.Read(new MemoryStream(truncated)),
                "A header truncated before format/track-count/division must throw InvalidMidiFileException.");
        }

        [Test]
        [Description("A file truncated mid-track (declared MTrk length exceeds the remaining bytes) "
                   + "must throw InvalidMidiFileException.")]
        public void Read_TruncatedTrackBody_ThrowsInvalidMidiFileException() {
            byte[] track = new MidiTrackEventBuilder()
                .NoteOn(0, 0, 60, 100)
                .NoteOff(240, 0, 60)
                .EndOfTrack()
                .BuildChunk();
            byte[] file = MidiTestBuilder.BuildFile(480, track);
            byte[] truncated = new byte[file.Length - 5];
            System.Array.Copy(file, truncated, truncated.Length);

            Assert.Throws<InvalidMidiFileException>(
                () => MidiFile.Read(new MemoryStream(truncated)),
                "A truncated MTrk body must throw InvalidMidiFileException.");
        }

        [Test]
        [Description("A header declaring more tracks than are actually present must throw "
                   + "InvalidMidiFileException rather than silently returning fewer tracks.")]
        public void Read_DeclaredTrackCountExceedsActual_ThrowsInvalidMidiFileException() {
            byte[] track = new MidiTrackEventBuilder().EndOfTrack().BuildChunk();
            byte[] header = MidiTestBuilder.BuildHeaderChunk(1, 2, 480);
            byte[] file = Concat(header, track);

            Assert.Throws<InvalidMidiFileException>(
                () => MidiFile.Read(new MemoryStream(file)),
                "A declared track count of 2 with only 1 MTrk chunk present must throw InvalidMidiFileException.");
        }

        [Test]
        [Description("Loading from a non-seekable stream must succeed by buffering internally, mirroring "
                   + "the SF2 loader's forward-only path.")]
        public void Read_NonSeekableStream_LoadsSuccessfully() {
            byte[] track = new MidiTrackEventBuilder().NoteOn(0, 0, 60, 100).EndOfTrack().BuildChunk();
            byte[] file = MidiTestBuilder.BuildFile(480, track);

            using NonSeekableStream stream = new NonSeekableStream(file);
            MidiFile parsed = MidiFile.Read(stream);

            Assert.That(parsed.Tracks[0].Messages, Has.Length.EqualTo(2));
        }

        static byte[] Concat(params byte[][] parts) {
            int total = 0;
            foreach (byte[] part in parts)
                total += part.Length;

            byte[] result = new byte[total];
            int offset = 0;
            foreach (byte[] part in parts) {
                part.CopyTo(result, offset);
                offset += part.Length;
            }
            return result;
        }
    }
}

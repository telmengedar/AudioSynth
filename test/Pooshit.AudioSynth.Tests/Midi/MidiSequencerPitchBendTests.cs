using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="MidiSequencer.Render"/> PitchWheel tests (DiVoid #7140): the 14-bit decode and
    /// GM ±2 semitone conversion, mirroring <see cref="MidiSequencerChannelGainTests"/>.
    /// </summary>
    [TestFixture]
    public class MidiSequencerPitchBendTests {

        static readonly AudioFormat Format = new AudioFormat(44100, 2);

        static TimedMessageSequence BuildSequence(params MidiTrackEventBuilder[] builders) {
            byte[][] chunks = new byte[builders.Length][];
            for (int i = 0; i < builders.Length; i++)
                chunks[i] = builders[i].EndOfTrack().BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, chunks)));
            return new TimedMessageSequence(file);
        }

        static SoundBank SinglePresetBank() {
            StubPatch piano = new StubPatch("piano");
            return new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
                (128, 0, "piano", (IPatch)piano),
            });
        }

        [Test]
        [Description("A centered PitchWheel value (8192) must decode to a 0-semitone bend.")]
        public void Render_CenteredPitchWheel_ProducesZeroSemitoneBend() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().PitchWheel(0, 0, 8192));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1));
            (int channel, float semitones) = synth.ChannelPitchBendCalls[0];
            Assert.That(channel, Is.EqualTo(0));
            Assert.That(semitones, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        [Description("Full-up PitchWheel (16383) must decode to approximately +2 semitones (conventional asymmetric-by-one-LSB result).")]
        public void Render_FullUpPitchWheel_ProducesApproximatelyPlusTwoSemitones() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().PitchWheel(0, 2, 16383));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1));
            (int channel, float semitones) = synth.ChannelPitchBendCalls[0];
            Assert.That(channel, Is.EqualTo(2));
            Assert.That(semitones, Is.EqualTo(2f).Within(0.001f));
        }

        [Test]
        [Description("Full-down PitchWheel (0) must decode to exactly -2 semitones.")]
        public void Render_FullDownPitchWheel_ProducesMinusTwoSemitones() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().PitchWheel(0, 5, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1));
            (int channel, float semitones) = synth.ChannelPitchBendCalls[0];
            Assert.That(channel, Is.EqualTo(5));
            Assert.That(semitones, Is.EqualTo(-2f).Within(1e-6f));
        }

        [Test]
        [Description("A non-PitchWheel message must not add a pitch-bend call.")]
        public void Render_NonPitchWheelMessage_AddsNoPitchBendCall() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 7, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Is.Empty);
        }

        [Test]
        [Description("RPN 0 (CC101=0, CC100=0, CC6=<range>) must widen the channel's bend range so a full-up " +
                     "PitchWheel decodes against it instead of the GM ±2 default (DiVoid #7209/#7210).")]
        public void Render_Rpn0SetsRange12_FullUpPitchWheelProducesApproximatelyPlusTwelveSemitones() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 6, 101, 0)
                    .Controller(0, 6, 100, 0)
                    .Controller(0, 6, 6, 12)
                    .PitchWheel(0, 6, 16383));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1));
            (int channel, float semitones) = synth.ChannelPitchBendCalls[0];
            Assert.That(channel, Is.EqualTo(6));
            Assert.That(semitones, Is.EqualTo(12f).Within(0.01f));
        }

        [Test]
        [Description("RPN-null (CC101=127, CC100=127) must close the Data Entry window: a CC6 sent after RPN-null " +
                     "must not change a previously-armed bend range (DiVoid #7210 §6).")]
        public void Render_Rpn0SetsRange12_ThenRpnNull_SubsequentDataEntryIsIgnored() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 6, 101, 0)
                    .Controller(0, 6, 100, 0)
                    .Controller(0, 6, 6, 12)
                    .Controller(0, 6, 101, 127)
                    .Controller(0, 6, 100, 127)
                    .Controller(0, 6, 6, 2)
                    .PitchWheel(0, 6, 16383));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1));
            (int channel, float semitones) = synth.ChannelPitchBendCalls[0];
            Assert.That(channel, Is.EqualTo(6));
            Assert.That(semitones, Is.EqualTo(12f).Within(0.01f),
                "the CC6=2 sent after RPN-null must be ignored; the range armed while RPN 0 was selected must stick.");
        }

        [Test]
        [Description("A song that never sends RPN must decode PitchWheel identically (bit-for-bit) to the " +
                     "pre-RPN hardcoded ±2 semitone constant: same operand and order preserves IEEE-754 equality.")]
        public void Render_NoRpnSent_FullUpPitchWheel_IsBitIdenticalToPreChangeConstant() {
            const int value14 = 16383;
            const float legacyRange = 2f;
            float expected = (value14 - 8192) / (float)8192 * legacyRange;

            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().PitchWheel(0, 3, value14));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1));
            (int channel, float semitones) = synth.ChannelPitchBendCalls[0];
            Assert.That(channel, Is.EqualTo(3));
            Assert.That(semitones, Is.EqualTo(expected), "no-RPN decode must remain bit-identical to the pre-change constant.");
        }
    }
}

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
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)piano),
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
    }
}

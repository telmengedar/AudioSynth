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
    /// <see cref="MidiSequencer.Render"/> CC7/CC11 tests (DiVoid #7126): GM reset seeds the default
    /// gain curve, a <c>Controller</c> message recomputes it, and unrelated CC numbers stay ignored.
    /// </summary>
    [TestFixture]
    public class MidiSequencerChannelGainTests {

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
        [Description("Render GM-resets every channel's mix gain to the GM default CC7=100/CC11=127 curve " +
                     "value (~0.62), one call per channel, before any event is applied.")]
        public void Render_Always_GmResetsAllChannelGainsToDefaultCurveValue() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelGainCalls, Has.Count.GreaterThanOrEqualTo(16));
            for (int channel = 0; channel < 16; channel++) {
                (int Channel, float Gain) call = synth.ChannelGainCalls[channel];
                Assert.That(call.Channel, Is.EqualTo(channel));
                Assert.That(call.Gain, Is.EqualTo(0.6200f).Within(0.001f),
                    $"channel {channel} must GM-reset to the CC7=100/CC11=127 default gain (~0.62).");
            }
        }

        [Test]
        [Description("A CC7 (Volume) Controller message recomputes the channel gain against the current " +
                     "CC11, holding the GM-default expression, so CC7=127 yields unity gain.")]
        public void Render_Cc7Controller_RecomputesGainAgainstCurrentExpression() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 7, 127));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Gain) call = synth.ChannelGainCalls[16];
            Assert.That(call.Channel, Is.EqualTo(0));
            Assert.That(call.Gain, Is.EqualTo(1.0f).Within(0.0001f),
                "CC7=127 with the GM-default CC11=127 must yield unity gain.");
        }

        [Test]
        [Description("A CC7 value of 0 must produce a channel gain of exactly zero (the composer muting the channel).")]
        public void Render_Cc7Zero_ProducesZeroGain() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 3, 7, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Gain) call = synth.ChannelGainCalls[16];
            Assert.That(call.Channel, Is.EqualTo(3));
            Assert.That(call.Gain, Is.EqualTo(0f));
        }

        [Test]
        [Description("A Controller message for a CC number other than 7 or 11 must not touch the channel gain.")]
        public void Render_OtherController_DoesNotChangeChannelGain() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 1, 64)); // modulation wheel, not volume/expression

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelGainCalls, Has.Count.EqualTo(16),
                "only the 16 GM-reset gain calls are expected; an unrelated controller must not add one.");
        }
    }
}

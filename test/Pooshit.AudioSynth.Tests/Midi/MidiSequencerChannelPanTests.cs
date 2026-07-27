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
    /// <see cref="MidiSequencer.Render"/> CC10 (Pan) tests (DiVoid #7127): GM reset seeds centre pan,
    /// a <c>Controller</c> message maps CC10 to <see cref="ISynthesizer.SetChannelPan"/>, and unrelated
    /// CC numbers stay ignored, mirroring <see cref="MidiSequencerChannelGainTests"/>.
    /// </summary>
    [TestFixture]
    public class MidiSequencerChannelPanTests {

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
        [Description("Render GM-resets every channel's pan to centre (0), one call per channel, before any event is applied.")]
        public void Render_Always_GmResetsAllChannelPansToCentre() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPanCalls, Has.Count.GreaterThanOrEqualTo(16));
            for (int channel = 0; channel < 16; channel++) {
                (int Channel, float Pan) call = synth.ChannelPanCalls[channel];
                Assert.That(call.Channel, Is.EqualTo(channel));
                Assert.That(call.Pan, Is.EqualTo(0f),
                    $"channel {channel} must GM-reset to centre pan (0).");
            }
        }

        [Test]
        [Description("CC10=0 (hard left) maps to pan=-1.")]
        public void Render_Cc10Zero_MapsToFullLeft() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 10, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Pan) call = synth.ChannelPanCalls[16];
            Assert.That(call.Channel, Is.EqualTo(0));
            Assert.That(call.Pan, Is.EqualTo(-1f).Within(1e-6f),
                "CC10=0 must map to pan=-1 (hard left).");
        }

        [Test]
        [Description("CC10=64 (centre) maps to pan=0.")]
        public void Render_Cc10Center_MapsToZero() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 1, 10, 64));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Pan) call = synth.ChannelPanCalls[16];
            Assert.That(call.Channel, Is.EqualTo(1));
            Assert.That(call.Pan, Is.EqualTo(0f).Within(1e-6f),
                "CC10=64 must map to pan=0 (centre).");
        }

        [Test]
        [Description("CC10=127 (full up) maps to approximately +0.984, just short of the hard-right rail.")]
        public void Render_Cc10FullUp_MapsToApproximatelyPlusOne() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 2, 10, 127));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Pan) call = synth.ChannelPanCalls[16];
            Assert.That(call.Channel, Is.EqualTo(2));
            Assert.That(call.Pan, Is.EqualTo(0.984f).Within(0.001f),
                "CC10=127 must map to approximately +0.984 under the symmetric (value-64)/64 mapping.");
        }

        [Test]
        [Description("A CC7 (Volume) Controller message must not touch channel pan.")]
        public void Render_Cc7Controller_DoesNotChangeChannelPan() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 7, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPanCalls, Has.Count.EqualTo(16),
                "only the 16 GM-reset pan calls are expected; CC7 must not add one.");
        }
    }
}

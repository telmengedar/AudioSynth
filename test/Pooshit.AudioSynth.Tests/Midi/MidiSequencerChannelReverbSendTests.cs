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
    /// <see cref="MidiSequencer.Render"/> CC91 (reverb send) tests (DiVoid #7165/#7170): GM reset seeds
    /// the GM1 default send (40/127), a <c>Controller</c> message maps CC91 to
    /// <see cref="ISynthesizer.SetChannelReverbSend"/>, and unrelated CC numbers stay ignored, mirroring
    /// <see cref="MidiSequencerChannelPanTests"/>.
    /// </summary>
    [TestFixture]
    public class MidiSequencerChannelReverbSendTests {

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
        [Description("Render GM-resets every channel's reverb send to the GM1 default (40/127), one call per channel, before any event is applied.")]
        public void Render_Always_GmResetsAllChannelReverbSendsToDefault() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelReverbSendCalls, Has.Count.GreaterThanOrEqualTo(16));
            for (int channel = 0; channel < 16; channel++) {
                (int Channel, float Level) call = synth.ChannelReverbSendCalls[channel];
                Assert.That(call.Channel, Is.EqualTo(channel));
                Assert.That(call.Level, Is.EqualTo(40f / 127f).Within(1e-6f),
                    $"channel {channel} must GM-reset to the default reverb send (40/127).");
            }
        }

        [Test]
        [Description("CC91=127 (full send) maps to reverb-send level=1.")]
        public void Render_Cc91FullScale_MapsToOne() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 91, 127));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Level) call = synth.ChannelReverbSendCalls[16];
            Assert.That(call.Channel, Is.EqualTo(0));
            Assert.That(call.Level, Is.EqualTo(1f).Within(1e-6f),
                "CC91=127 must map to reverb-send level=1 (full send).");
        }

        [Test]
        [Description("CC91=0 (no send) maps to reverb-send level=0.")]
        public void Render_Cc91Zero_MapsToZero() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 1, 91, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Level) call = synth.ChannelReverbSendCalls[16];
            Assert.That(call.Channel, Is.EqualTo(1));
            Assert.That(call.Level, Is.EqualTo(0f).Within(1e-6f),
                "CC91=0 must map to reverb-send level=0 (fully dry).");
        }

        [Test]
        [Description("A CC7 (Volume) Controller message must not touch channel reverb send.")]
        public void Render_Cc7Controller_DoesNotChangeChannelReverbSend() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 7, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelReverbSendCalls, Has.Count.EqualTo(16),
                "only the 16 GM-reset reverb-send calls are expected; CC7 must not add one.");
        }
    }
}

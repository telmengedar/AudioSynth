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
    /// <see cref="MidiSequencer.Render"/> CC93 (chorus send) tests (DiVoid #7188, design #7190): GM reset
    /// seeds the GM default send (0/127, chorus off unless the song asks), a <c>Controller</c> message maps
    /// CC93 to <see cref="ISynthesizer.SetChannelChorusSend"/>, and unrelated CC numbers stay ignored,
    /// mirroring <see cref="MidiSequencerChannelReverbSendTests"/>.
    /// </summary>
    [TestFixture]
    public class MidiSequencerChannelChorusSendTests {

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
        [Description("Render GM-resets every channel's chorus send to the GM default (0/127, chorus off), one call per channel, before any event is applied.")]
        public void Render_Always_GmResetsAllChannelChorusSendsToDefault() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelChorusSendCalls, Has.Count.GreaterThanOrEqualTo(16));
            for (int channel = 0; channel < 16; channel++) {
                (int Channel, float Level) call = synth.ChannelChorusSendCalls[channel];
                Assert.That(call.Channel, Is.EqualTo(channel));
                Assert.That(call.Level, Is.EqualTo(0f),
                    $"channel {channel} must GM-reset to the default chorus send (0, off).");
            }
        }

        [Test]
        [Description("CC93=127 (full send) maps to chorus-send level=1.")]
        public void Render_Cc93FullScale_MapsToOne() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 93, 127));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Level) call = synth.ChannelChorusSendCalls[16];
            Assert.That(call.Channel, Is.EqualTo(0));
            Assert.That(call.Level, Is.EqualTo(1f).Within(1e-6f),
                "CC93=127 must map to chorus-send level=1 (full send).");
        }

        [Test]
        [Description("CC93=0 (no send) maps to chorus-send level=0.")]
        public void Render_Cc93Zero_MapsToZero() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 1, 93, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int Channel, float Level) call = synth.ChannelChorusSendCalls[16];
            Assert.That(call.Channel, Is.EqualTo(1));
            Assert.That(call.Level, Is.EqualTo(0f).Within(1e-6f),
                "CC93=0 must map to chorus-send level=0 (fully dry).");
        }

        [Test]
        [Description("A CC7 (Volume) Controller message must not touch channel chorus send.")]
        public void Render_Cc7Controller_DoesNotChangeChannelChorusSend() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 7, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelChorusSendCalls, Has.Count.EqualTo(16),
                "only the 16 GM-reset chorus-send calls are expected; CC7 must not add one.");
        }

        [Test]
        [Description("A CC91 (reverb send) Controller message must not touch channel chorus send.")]
        public void Render_Cc91Controller_DoesNotChangeChannelChorusSend() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 91, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelChorusSendCalls, Has.Count.EqualTo(16),
                "only the 16 GM-reset chorus-send calls are expected; CC91 must not add one.");
        }
    }
}

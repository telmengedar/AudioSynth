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
    /// <see cref="MidiSequencer.Render"/> CC1 (modulation wheel) tests (DiVoid #7181, design #7180):
    /// a <c>Controller</c> message maps CC1 to <see cref="ISynthesizer.SetChannelModulation"/>, unrelated
    /// CC numbers are ignored, and — unlike CC7/CC10/CC11/CC91 — modulation is deliberately absent from
    /// the GM-reset loop (design §10/§14, mirroring sustain), so a render with no CC1 events produces
    /// zero <see cref="RecordingSynthesizer.ChannelModulationCalls"/>.
    /// </summary>
    [TestFixture]
    public class MidiSequencerModulationTests {

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
        [Description("A render with no CC1 events must produce zero SetChannelModulation calls: modulation " +
                     "is deliberately not part of the GM-reset loop (design §10), unlike pan/gain/reverb-send.")]
        public void Render_NoModulationEvents_ProducesZeroChannelModulationCalls() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelModulationCalls, Is.Empty,
                "modulation must not be GM-reset; only an actual CC1 event may produce a call.");
        }

        [Test]
        [Description("CC1=127 (wheel fully up) maps to modulation amount=1.")]
        public void Render_Cc1FullScale_MapsToOne() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 1, 127));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelModulationCalls, Has.Count.EqualTo(1));
            (int channel, float amount) = synth.ChannelModulationCalls[0];
            Assert.That(channel, Is.EqualTo(0));
            Assert.That(amount, Is.EqualTo(1f).Within(1e-6f), "CC1=127 must map to modulation amount=1.");
        }

        [Test]
        [Description("CC1=0 (wheel fully down) maps to modulation amount=0.")]
        public void Render_Cc1Zero_MapsToZero() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 1, 1, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelModulationCalls, Has.Count.EqualTo(1));
            (int channel, float amount) = synth.ChannelModulationCalls[0];
            Assert.That(channel, Is.EqualTo(1));
            Assert.That(amount, Is.EqualTo(0f).Within(1e-6f), "CC1=0 must map to modulation amount=0.");
        }

        [Test]
        [Description("A mid-range CC1 value maps to value/127.")]
        public void Render_Cc1MidRange_MapsToValueOverFullScale() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 2, 1, 64));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelModulationCalls, Has.Count.EqualTo(1));
            (int channel, float amount) = synth.ChannelModulationCalls[0];
            Assert.That(channel, Is.EqualTo(2));
            Assert.That(amount, Is.EqualTo(64f / 127f).Within(1e-6f),
                "CC1=64 must map to modulation amount=64/127.");
        }

        [Test]
        [Description("A CC91 (reverb send) Controller message must not touch channel modulation.")]
        public void Render_Cc91Controller_DoesNotChangeChannelModulation() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 91, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelModulationCalls, Is.Empty,
                "CC91 must not produce a SetChannelModulation call.");
        }
    }
}

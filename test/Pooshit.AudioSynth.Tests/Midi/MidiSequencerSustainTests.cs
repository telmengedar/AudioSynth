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
    /// <see cref="MidiSequencer.Render"/> CC64 (sustain/hold pedal) tests (DiVoid #7155, design #7179):
    /// a <c>Controller</c> message maps CC64 to <see cref="ISynthesizer.SetChannelSustain"/> at the
    /// standard MIDI threshold (&gt;=64 = down), unrelated CC numbers are ignored, and — unlike CC7/CC10/
    /// CC11/CC91 — sustain is deliberately absent from the GM-reset loop (design §10/§14), so a render
    /// with no CC64 events produces zero <see cref="RecordingSynthesizer.ChannelSustainCalls"/>.
    /// </summary>
    [TestFixture]
    public class MidiSequencerSustainTests {

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
        [Description("A render with no CC64 events must produce zero SetChannelSustain calls: sustain is " +
                     "deliberately not part of the GM-reset loop (design §10), unlike pan/gain/reverb-send.")]
        public void Render_NoSustainEvents_ProducesZeroChannelSustainCalls() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelSustainCalls, Is.Empty,
                "sustain must not be GM-reset; only an actual CC64 event may produce a call.");
        }

        [Test]
        [Description("CC64=127 (pedal fully down) maps to SetChannelSustain(channel, held: true).")]
        public void Render_Cc64FullScale_MapsToHeldTrue() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 64, 127));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelSustainCalls, Has.Count.EqualTo(1));
            (int channel, bool held) = synth.ChannelSustainCalls[0];
            Assert.That(channel, Is.EqualTo(0));
            Assert.That(held, Is.True, "CC64=127 must engage the sustain pedal.");
        }

        [Test]
        [Description("CC64=0 (pedal fully up) maps to SetChannelSustain(channel, held: false).")]
        public void Render_Cc64Zero_MapsToHeldFalse() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 1, 64, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelSustainCalls, Has.Count.EqualTo(1));
            (int channel, bool held) = synth.ChannelSustainCalls[0];
            Assert.That(channel, Is.EqualTo(1));
            Assert.That(held, Is.False, "CC64=0 must disengage the sustain pedal.");
        }

        [Test]
        [Description("CC64=63 is just below the MIDI sustain threshold and must map to held=false.")]
        public void Render_Cc64JustBelowThreshold_MapsToHeldFalse() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 2, 64, 63));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int channel, bool held) = synth.ChannelSustainCalls[0];
            Assert.That(channel, Is.EqualTo(2));
            Assert.That(held, Is.False, "CC64=63 (below the 64 threshold) must map to held=false.");
        }

        [Test]
        [Description("CC64=64 is exactly at the MIDI sustain threshold and must map to held=true.")]
        public void Render_Cc64AtThreshold_MapsToHeldTrue() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 3, 64, 64));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int channel, bool held) = synth.ChannelSustainCalls[0];
            Assert.That(channel, Is.EqualTo(3));
            Assert.That(held, Is.True, "CC64=64 (at the threshold) must map to held=true.");
        }

        [Test]
        [Description("A CC91 (reverb send) Controller message must not touch channel sustain.")]
        public void Render_Cc91Controller_DoesNotChangeChannelSustain() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 0, 91, 100));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelSustainCalls, Is.Empty,
                "CC91 must not produce a SetChannelSustain call.");
        }
    }
}

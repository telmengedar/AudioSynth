using System;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof test for the chorus master insert (DiVoid #7188, design #7190 §14.9): no
    /// committed corpus <c>.mid</c> carries CC93, so the proof is asset-free — a
    /// <see cref="TimedMessageSequence"/> is built programmatically with a CC93 event ahead of a
    /// sustained note, rendered end-to-end through <see cref="MidiSequencer.Render"/> and a real
    /// <see cref="SamplePatch"/>, and the chorused-channel output must differ from the identical render
    /// with CC93 left at its GM-reset default (0, chorus off). Mirrors the asset-free backstop role
    /// <see cref="ReverbSendRoutingTests"/> plays alongside <see cref="ReverbRenderProofTests"/>.
    /// </summary>
    [TestFixture]
    public class ChorusRenderProofTests {

        const int SampleRate = 44100;
        const int NoteDurationTicks = 4000;
        const int TicksPerQuarterNote = 480;

        static TimedMessageSequence BuildSequence(bool sendChorus) {
            MidiTrackEventBuilder builder = new MidiTrackEventBuilder();
            if (sendChorus)
                builder.Controller(0, 0, 93, 127);
            builder.NoteOn(0, 0, 60, 100)
                   .NoteOff(NoteDurationTicks, 0, 60)
                   .EndOfTrack();

            byte[] chunk = builder.BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(TicksPerQuarterNote, chunk)));
            return new TimedMessageSequence(file);
        }

        static float[] RenderSequence(bool sendChorus) {
            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: ChorusSettings.Default);
            SampleRegion region = BuildSustainedDcRegion(0.3f, 8192);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            SoundBank bank = new SoundBank(new[] { (0, 0, "", (IPatch)patch), (128, 0, "", (IPatch)patch) });
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            TimedMessageSequence sequence = BuildSequence(sendChorus);
            MidiSequencer.Render(sequence, synth, sink, bank);
            return sink.ToArray();
        }

        static SampleRegion BuildSustainedDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float DifferenceRms(float[] a, float[] b) {
            int length = Math.Min(a.Length, b.Length);
            double sum = 0.0;
            for (int i = 0; i < length; i++) {
                double diff = (double)a[i] - b[i];
                sum += diff * diff;
            }
            return (float)Math.Sqrt(sum / length);
        }

        [Test]
        [Description("A CC93=127 sequence rendered through MidiSequencer.Render must diverge measurably from " +
                     "the identical sequence left at the GM-reset default (CC93=0, chorus off): CC93 must " +
                     "actually reach the chorus end-to-end through the sequencer's GM routing.")]
        public void Cc93Sequence_RendersDifferentlyFromDefaultChorusOffRender() {
            float[] chorused = RenderSequence(sendChorus: true);
            float[] dry = RenderSequence(sendChorus: false);

            Assert.That(chorused.Length, Is.EqualTo(dry.Length), "chorus routing must not change the rendered frame count.");

            Assert.That(chorused, Is.Not.EqualTo(dry),
                "a CC93=127 render must not be bit-identical to the GM-reset (CC93=0) render.");

            float diffRms = DifferenceRms(chorused, dry);
            TestContext.WriteLine($"Chorused-vs-default difference RMS: {diffRms:F6}.");
            Assert.That(diffRms, Is.GreaterThan(0f),
                "the CC93-driven render must carry measurable chorus energy the default (off) render lacks.");
        }
    }
}

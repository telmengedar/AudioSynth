using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression tests for the class-B declick defect family (catalog DiVoid #6272 §B): a note must
    /// ramp in at onset and fade out on release through the full voice/synthesizer path, with no
    /// full-scale discontinuity at either edge.  A DC source isolates the amplitude contour so any
    /// edge jump is directly visible as an anomalous consecutive-sample delta.
    /// </summary>
    [TestFixture]
    public class EnvelopeDeclickTests {

        const int SampleRate = 44100;
        const float DcValue = 0.8f;
        const float DeltaEpsilon = 0.02f;

        static SampleRegion BuildDcRegion(EnvelopeParameters envelope) {
            float[] buf = new float[SampleRate];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = DcValue;
            return new SampleRegion(buf, 0, buf.Length, 0, buf.Length, LoopMode.Continuous, SampleRate, 60, 0, envelope);
        }

        static float MaxConsecutiveDelta(float[] samples, int from, int to) {
            float max = 0f;
            for (int i = from + 1; i < to; i++) {
                float delta = Math.Abs(samples[i] - samples[i - 1]);
                if (delta > max)
                    max = delta;
            }
            return max;
        }

        [Test]
        [Description("Onset: the note ramps in with no full-scale jump at the first samples.")]
        public void Onset_RampsIn_NoFullScaleJump() {
            EnvelopeParameters envelope = new EnvelopeParameters(0f, 0.01f, 0f, 0f, 1f, 0.01f);
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, 64, 16);
            Synthesizer synth = new Synthesizer(opts, new SamplePatch(BuildDcRegion(envelope), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, 2000);
            float[] samples = sink.ToArray();

            Assert.That(samples[0], Is.LessThan(0.1f), $"onset started at {samples[0]}; expected a ramp from near zero.");
            Assert.That(MaxConsecutiveDelta(samples, 0, samples.Length), Is.LessThan(DeltaEpsilon),
                "a consecutive-sample delta above epsilon indicates an onset jump or zipper.");
        }

        [Test]
        [Description("Note-off: the release produces a gradual fade to zero, not a full-scale discontinuity.")]
        public void NoteOff_ReleaseFades_NoDiscontinuity() {
            EnvelopeParameters envelope = new EnvelopeParameters(0f, 0.005f, 0f, 0f, 1f, 0.05f);
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, 64, 16);
            Synthesizer synth = new Synthesizer(opts, new SamplePatch(BuildDcRegion(envelope), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, 500);
            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, 4000);
            float[] samples = sink.ToArray();

            Assert.That(MaxConsecutiveDelta(samples, 0, samples.Length), Is.LessThan(DeltaEpsilon),
                "a consecutive-sample delta above epsilon at the note-off edge indicates a hard cut, not a fade.");

            float preRelease = Math.Abs(samples[480]);
            float midRelease = Math.Abs(samples[1500]);
            Assert.That(midRelease, Is.LessThan(preRelease),
                "the release tail must descend below the sustained level.");

            float tailPeak = 0f;
            for (int i = samples.Length - 100; i < samples.Length; i++)
                tailPeak = Math.Max(tailPeak, Math.Abs(samples[i]));
            Assert.That(tailPeak, Is.LessThan(1e-4f), $"release did not converge to silence; tail peak={tailPeak}.");
        }
    }
}

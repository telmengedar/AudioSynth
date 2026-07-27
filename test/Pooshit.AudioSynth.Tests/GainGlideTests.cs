using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression tests for INV-1: the gain glide must be continuous across internal block
    /// boundaries.  A DC (constant-value) source is used so the only output variation is
    /// the gain ramp; any spike at a block boundary is immediately visible as an anomalous
    /// consecutive-sample delta.
    /// </summary>
    public class GainGlideTests {

        const int SampleRate = 44100;
        const int InternalBlockFrames = 64;
        const float DcValue = 0.8f;

        static SampleRegion BuildDcRegion(int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = DcValue;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        [Test]
        public void GainGlideHasNoDeltaSpikeAcrossInternalBlockBoundary() {
            int framesToRender = InternalBlockFrames * 3;
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, InternalBlockFrames, 16);
            SampleRegion region = BuildDcRegion(framesToRender * 4);
            SamplePatch patch = new SamplePatch(region, SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, framesToRender);

            float[] samples = sink.ToArray();
            Assert.That(samples.Length, Is.EqualTo(framesToRender));

            const float epsilon = 0.05f;
            float maxDelta = 0f;
            for (int i = 0; i < samples.Length - 1; i++) {
                float delta = Math.Abs(samples[i + 1] - samples[i]);
                maxDelta = Math.Max(maxDelta, delta);
            }

            Assert.That(maxDelta, Is.LessThan(epsilon),
                $"consecutive-sample delta exceeded epsilon={epsilon}; max observed={maxDelta}. " +
                $"A value at or above epsilon indicates a block-boundary zipper.");

            float deltaAtBoundary = Math.Abs(samples[InternalBlockFrames] - samples[InternalBlockFrames - 1]);
            Assert.That(deltaAtBoundary, Is.LessThan(epsilon),
                $"delta at internal block boundary (index {InternalBlockFrames}) was {deltaAtBoundary}; " +
                $"expected <{epsilon} (same as surrounding frames).");
        }

        [Test]
        public void GainGlideIsMonotonicDuringNoteOn() {
            int framesToRender = 300;
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, InternalBlockFrames, 16);
            SampleRegion region = BuildDcRegion(framesToRender * 4);
            SamplePatch patch = new SamplePatch(region, SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, framesToRender);
            float[] samples = sink.ToArray();

            bool hasIncrease = false;
            for (int i = 1; i < samples.Length; i++) {
                float diff = samples[i] - samples[i - 1];
                Assert.That(diff, Is.GreaterThanOrEqualTo(-1e-5f),
                    $"gain decreased during note-on ramp at sample index {i}: {samples[i - 1]} -> {samples[i]}");
                if (diff > 0f)
                    hasIncrease = true;
            }
            Assert.That(hasIncrease, Is.True, "gain never increased; ramp appears stuck at zero");
        }

        [Test]
        public void GainGlideConvergesToZeroAfterRelease() {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, InternalBlockFrames, 16);
            int bufLen = 44100;
            SampleRegion region = BuildDcRegion(bufLen);
            SamplePatch patch = new SamplePatch(region, SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, 500);
            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, 500);

            float[] samples = sink.ToArray();
            float tailPeak = 0f;
            for (int i = samples.Length - 100; i < samples.Length; i++)
                tailPeak = Math.Max(tailPeak, Math.Abs(samples[i]));

            Assert.That(tailPeak, Is.LessThan(1e-4f),
                $"gain did not converge to zero after NoteOff + release tail; tail peak={tailPeak}");
        }
    }
}

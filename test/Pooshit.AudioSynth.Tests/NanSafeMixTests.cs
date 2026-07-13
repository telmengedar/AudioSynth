using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression tests for INV-2: the synthesizer's finalize choke point must map any NaN or Inf
    /// produced by a voice to zero and clamp all output to [-1, 1] before it leaves <c>Read</c>.
    /// Uses a test-only NaN/Inf-emitting patch so no real sample data is involved.
    /// </summary>
    public class NanSafeMixTests {

        [Test]
        public void NanAndInfFromVoiceAreAbsentInOutput() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            NanEmittingPatch patch = new NanEmittingPatch();
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, 512);
            float[] samples = sink.ToArray();

            for (int i = 0; i < samples.Length; i++) {
                float s = samples[i];
                Assert.That(float.IsNaN(s), Is.False, $"NaN at output sample index {i}");
                Assert.That(float.IsInfinity(s), Is.False, $"Inf at output sample index {i}");
            }
        }

        [Test]
        public void OutputIsBoundedAfterNanAndInfInjection() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            NanEmittingPatch patch = new NanEmittingPatch();
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, 512);
            float[] samples = sink.ToArray();

            foreach (float s in samples)
                Assert.That(Math.Abs(s), Is.LessThanOrEqualTo(1f),
                    $"sample out of [-1,1] after NaN/Inf injection: {s}");
        }

        [Test]
        public void NanVoiceOutputMapToZeroNotNan() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 1, 64, 16);
            NanEmittingPatch patch = new NanEmittingPatch();
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, 256);
            float[] samples = sink.ToArray();

            bool allZero = true;
            foreach (float s in samples) {
                Assert.That(float.IsNaN(s), Is.False, $"NaN reached the output; finalize choke failed");
                if (s != 0f)
                    allZero = false;
            }
            Assert.That(allZero, Is.True,
                "expected all output to be zero (NaN/Inf → 0 by finalize); found non-zero samples");
        }
    }
}

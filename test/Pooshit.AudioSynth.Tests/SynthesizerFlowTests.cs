using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    public class SynthesizerFlowTests {

        static SampleRegion BuildDcRegion(float value, int length, int sampleRate) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, sampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default);
        }

        [Test]
        public void NoteOnRendersCorrectFrameCount() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            SampleRegion region = BuildDcRegion(0.5f, 2048, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            long rendered = OfflineRenderer.Render(synth, sink, 1000);

            Assert.That(rendered, Is.EqualTo(1000));
            Assert.That(sink.SampleCount, Is.EqualTo(1000 * opts.Channels));
        }

        [Test]
        public void NoteOnProducesNonSilentOutput() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            SampleRegion region = BuildDcRegion(0.5f, 2048, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, 1000);
            float[] samples = sink.ToArray();

            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));

            Assert.That(peak, Is.GreaterThan(0.1f), $"output was silent; peak={peak}");
        }

        [Test]
        public void RenderedOutputIsAmplitudeBounded() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            SampleRegion region = BuildDcRegion(0.5f, 2048, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, 1000);
            float[] samples = sink.ToArray();

            foreach (float s in samples)
                Assert.That(Math.Abs(s), Is.LessThanOrEqualTo(1f), $"sample out of bounds: {s}");
        }

        [Test]
        public void SilenceWithNoActiveVoices() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 1, 64, 16);
            SampleRegion region = BuildDcRegion(0.8f, 2048, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            long rendered = OfflineRenderer.Render(synth, sink, 256);
            float[] samples = sink.ToArray();

            Assert.That(rendered, Is.EqualTo(256));
            foreach (float s in samples)
                Assert.That(s, Is.EqualTo(0f), "expected silence with no note on");
        }

        [Test]
        public void NoteOffReleasesVoiceIntoSilence() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 1, 64, 16);
            SampleRegion region = BuildDcRegion(0.5f, 2048, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, 300);
            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, 600);

            float[] samples = sink.ToArray();
            float tailPeak = 0f;
            for (int i = samples.Length - 100; i < samples.Length; i++)
                tailPeak = Math.Max(tailPeak, Math.Abs(samples[i]));

            Assert.That(tailPeak, Is.LessThan(1e-4f), $"voice still audible long after release; tail peak={tailPeak}");
        }

        [Test]
        public void ReadThrowsOnNonChannelAlignedSpan() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            SampleRegion region = BuildDcRegion(0.5f, 512, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);

            float[] buf = new float[3];
            Assert.Throws<ArgumentException>(() => synth.Read(buf.AsSpan()),
                "a stereo synthesizer given a 3-sample span must throw ArgumentException");
        }

        [Test]
        public void ReadAlignedOddFrameCountRendersWithoutThrowing() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            SampleRegion region = BuildDcRegion(0.5f, 512, 44100);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);

            synth.NoteOn(0, 60, 100);
            float[] buf = new float[6];
            int written = synth.Read(buf.AsSpan());

            Assert.That(written, Is.EqualTo(6), "Read must fill the full aligned span");
        }
    }
}

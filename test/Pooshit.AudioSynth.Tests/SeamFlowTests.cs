using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Audio.Sources;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// End-to-end proof of the central pull seam: a source is driven through the offline renderer into a sink.
    /// </summary>
    public class SeamFlowTests {

        [Test]
        public void SinePulledThroughRendererReachesSink() {
            AudioFormat format = new AudioFormat(44100, 2);
            SineSource source = new SineSource(format, 440.0, 0.5f);
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            long rendered = OfflineRenderer.Render(source, sink, 1000);

            Assert.That(rendered, Is.EqualTo(1000));
            Assert.That(sink.SampleCount, Is.EqualTo(2000));
        }

        [Test]
        public void RenderedSineIsNonSilentAndBounded() {
            AudioFormat format = new AudioFormat(44100, 1);
            SineSource source = new SineSource(format, 1000.0, 0.5f);
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            OfflineRenderer.Render(source, sink, 4096);
            float[] samples = sink.ToArray();

            float peak = 0f;
            foreach (float sample in samples)
                peak = Math.Max(peak, Math.Abs(sample));

            Assert.That(peak, Is.GreaterThan(0.4f), $"expected an audible sine, peak was {peak}");
            Assert.That(peak, Is.LessThanOrEqualTo(0.5f + 1e-4f), $"amplitude exceeded requested bound, peak was {peak}");
        }

        [Test]
        public void RendererRejectsFormatMismatch() {
            SineSource source = new SineSource(new AudioFormat(44100, 2), 440.0);
            InMemoryAudioSink sink = new InMemoryAudioSink(new AudioFormat(48000, 2));

            Assert.Throws<ArgumentException>(() => OfflineRenderer.Render(source, sink, 128));
        }
    }
}

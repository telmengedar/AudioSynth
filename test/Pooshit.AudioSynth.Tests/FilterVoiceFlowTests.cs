using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Full-path tests that the per-voice low-pass filter is actually applied inside the synthesizer.
    /// A region carrying a high-frequency source is rendered through an open filter and through a low
    /// cutoff; the low cutoff must attenuate the output.  This is the regression encoding of legacy
    /// defect #6243 (the SF2 low-pass filter was permanently disabled, leaving every patch too bright).
    /// </summary>
    [TestFixture]
    public class FilterVoiceFlowTests {

        const int SampleRate = 44100;

        static SampleRegion BuildToneRegion(float frequency, FilterParameters filter) {
            float[] buffer = new float[SampleRate];
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            return new SampleRegion(buffer, 0, buffer.Length, 0, buffer.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, filter, LfoParameters.Default, 0f);
        }

        static float[] Render(SampleRegion region, int frames) {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, 64, 16);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, frames);
            return sink.ToArray();
        }

        static float Rms(float[] samples, int from) {
            double sum = 0.0;
            int count = 0;
            for (int i = from; i < samples.Length; i++) {
                sum += (double)samples[i] * samples[i];
                count++;
            }
            return (float)Math.Sqrt(sum / count);
        }

        [Test]
        [Description("A low cutoff strongly attenuates a high-frequency source relative to an open filter.")]
        public void LowCutoff_AttenuatesHighFrequencySourceThroughSynth() {
            SampleRegion openRegion = BuildToneRegion(6000f, FilterParameters.Default);
            SampleRegion filteredRegion = BuildToneRegion(6000f,
                new FilterParameters(400f, FilterParameters.ButterworthResonance));

            float openRms = Rms(Render(openRegion, SampleRate / 2), 2000);
            float filteredRms = Rms(Render(filteredRegion, SampleRate / 2), 2000);

            Assert.That(openRms, Is.GreaterThan(0.1f), $"open render was unexpectedly quiet; rms={openRms}.");
            Assert.That(filteredRms, Is.LessThan(openRms * 0.25f),
                $"low cutoff did not attenuate the 6 kHz source; open rms={openRms}, filtered rms={filteredRms}.");
        }

        [Test]
        [Description("An open-filter render is non-silent, confirming the filter does not swallow signal when open.")]
        public void OpenFilter_RendersNonSilentSignal() {
            SampleRegion openRegion = BuildToneRegion(300f, FilterParameters.Default);

            float[] samples = Render(openRegion, SampleRate / 4);

            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));

            Assert.That(peak, Is.GreaterThan(0.3f), $"open filter produced a near-silent render; peak={peak}.");
        }
    }
}

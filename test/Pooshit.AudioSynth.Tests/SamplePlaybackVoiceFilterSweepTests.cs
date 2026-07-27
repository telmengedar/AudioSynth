using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Full-path tests for LFO-to-cutoff (filter-sweep) inside <see cref="SamplePlaybackVoice"/>: the
    /// HF-energy tracking proof and the #6272 §B zipper regression extended to this routing.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceFilterSweepTests {

        const int SampleRate = 44100;

        static SampleRegion BuildToneRegion(float frequency, FilterParameters filter, LfoParameters lfo, int bufferLength) {
            float[] buffer = new float[bufferLength];
            for (int i = 0; i < bufferLength; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            return new SampleRegion(buffer, 0, buffer.Length, 0, buffer.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, filter, lfo, 0f);
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

        static float WindowRms(float[] samples, int center, int halfWindow) {
            double sum = 0.0;
            int count = 0;
            for (int i = center - halfWindow; i < center + halfWindow; i++) {
                sum += (double)samples[i] * samples[i];
                count++;
            }
            return (float)Math.Sqrt(sum / count);
        }

        [Test]
        [Description("Deliverable proof: on a filtered preset with filter-sweep active, high-frequency energy " +
            "rises when the swept cutoff is near its peak (LFO at +1) and falls when the cutoff is near its " +
            "trough (LFO at -1) — a periodic HF-energy variation at the configured LFO rate.")]
        public void FilterSweep_HfEnergyTracksLfoValue_AtConfiguredRate() {
            const float rateHz = 2f;
            const float baseCutoffHz = 2000f;
            const float filterDepthCents = 3600f;
            const float toneHz = 6000f;

            LfoParameters lfoParameters = new LfoParameters(0f, rateHz, 0f, 0f, filterDepthCents);
            FilterParameters filterParameters = new FilterParameters(baseCutoffHz, FilterParameters.ButterworthResonance);
            SampleRegion region = BuildToneRegion(toneHz, filterParameters, lfoParameters, SampleRate * 2);

            float[] output = Render(region, SampleRate * 2);

            int periodFrames = (int)(SampleRate / rateHz);
            int peakFrame = periodFrames / 4;
            int troughFrame = (periodFrames * 3) / 4;
            int halfWindow = 500;

            float rmsAtPeak = WindowRms(output, peakFrame, halfWindow);
            float rmsAtTrough = WindowRms(output, troughFrame, halfWindow);

            Assert.That(rmsAtPeak, Is.GreaterThan(rmsAtTrough * 2f),
                $"HF energy at the LFO peak (cutoff swept up, rms={rmsAtPeak}) must exceed the trough " +
                $"(cutoff swept down, rms={rmsAtTrough}) by a wide margin.");
        }

        [Test]
        [Description("Regression for defect catalog #6272 §B (clicks/zipper class), extended to filter-sweep: " +
            "a control-rate coefficient recompute introduces no amplitude discontinuity, even under a wide sweep.")]
        public void ControlTick_IntroducesNoAmplitudeDiscontinuity() {
            LfoParameters lfo = new LfoParameters(0f, 7f, 0f, 0f, 2400f);
            FilterParameters filter = new FilterParameters(2000f, FilterParameters.ButterworthResonance);
            SampleRegion region = BuildToneRegion(200f, filter, lfo, 8 * SampleRate);
            float[] samples = Render(region, SampleRate);

            float maxDelta = 0f;
            for (int i = 1; i < samples.Length; i++)
                maxDelta = Math.Max(maxDelta, Math.Abs(samples[i] - samples[i - 1]));

            Assert.That(maxDelta, Is.LessThan(0.15f),
                $"max consecutive-sample delta {maxDelta} indicates a discontinuity at a control-rate tick boundary.");
        }
    }
}

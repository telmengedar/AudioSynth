using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable proof for the design's bit-identical no-regression invariant: with all three mod-LFO
    /// depths at zero (<see cref="LfoParameters.Default"/>), <see cref="SamplePlaybackVoice"/> must render
    /// byte-for-byte identical to the pre-tremolo/sweep (PR-8) pipeline — the tremolo multiplier is a
    /// structural no-op (<c>x * 1.0f == x</c>) and <see cref="BiquadLowPassFilter.SetCutoff"/> is never
    /// invoked.  The comparison oracle reconstructs the PR-8 pipeline independently from the same public
    /// primitives (<see cref="AmplitudeEnvelope"/>, <see cref="GainRamp"/>, <see cref="BiquadLowPassFilter"/>),
    /// so it does not merely re-run the production code path against itself.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceLfoNoRegressionTests {

        const int SampleRate = 44100;

        static SampleRegion BuildRegion(FilterParameters filter, EnvelopeParameters envelope, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = (float)Math.Sin(2.0 * Math.PI * 300.0 * i / SampleRate);
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                envelope, filter, LfoParameters.Default);
        }

        static float[] RenderPr8Baseline(SampleRegion region, float pitchIncrement, float targetGain, int frames) {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(region.Envelope, SampleRate);
            GainRamp gainRamp = new GainRamp(SampleRate);
            gainRamp.SetTarget(targetGain);
            BiquadLowPassFilter filter = new BiquadLowPassFilter(region.Filter, SampleRate);
            double readPos = region.Start;
            float[] output = new float[frames];

            for (int i = 0; i < frames; i++) {
                float gain = envelope.AdvanceFrame() * gainRamp.AdvanceFrame();

                int n = (int)readPos;
                float frac = (float)(readPos - n);
                float s0 = (n >= region.Start && n < region.End) ? region.Buffer[n] : 0f;
                int n1 = n + 1;
                float s1 = (n1 >= region.Start && n1 < region.End) ? region.Buffer[n1] : 0f;
                float sample = s0 + frac * (s1 - s0);

                readPos += pitchIncrement;
                double loopLen = region.LoopEnd - region.LoopStart;
                if (readPos >= region.LoopEnd) {
                    double excess = readPos - region.LoopStart;
                    readPos = region.LoopStart + (excess % loopLen);
                }

                sample = filter.Process(sample);
                output[i] = sample * gain;
            }

            return output;
        }

        [Test]
        [Description("All three depths zero renders bit-for-bit identical to an independently-reconstructed " +
            "PR-8 pipeline (envelope * gainRamp * filter, no tremolo multiplier, no SetCutoff retarget).")]
        public void AllDepthsZero_RendersBitIdenticalToPr8Baseline() {
            const float pitchIncrement = 1.03f;
            const float targetGain = 0.8f;
            FilterParameters filter = new FilterParameters(2000f, FilterParameters.ButterworthResonance);
            EnvelopeParameters envelope = new EnvelopeParameters(0f, 0.01f, 0f, 0.02f, 0.6f, 0.03f);
            SampleRegion region = BuildRegion(filter, envelope, 4000);

            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, pitchIncrement, targetGain, SampleRate);
            float[] actual = new float[3000];
            voice.RenderBlock(actual.AsSpan());

            float[] expected = RenderPr8Baseline(region, pitchIncrement, targetGain, actual.Length);

            for (int i = 0; i < expected.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]),
                    $"sample {i} diverged from the PR-8 baseline: actual={actual[i]}, expected={expected[i]}.");
        }
    }
}

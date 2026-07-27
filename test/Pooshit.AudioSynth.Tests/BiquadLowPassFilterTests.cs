using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="BiquadLowPassFilter"/>: the low-pass transfer function (high frequencies
    /// attenuated, low frequencies passed), the open-filter passthrough that guarantees no regression, and
    /// the stability guards that keep the section from producing NaN or infinity.  The transfer-function
    /// test is the regression encoding of legacy defect #6164 (a stale low-pass coefficient produced the
    /// wrong response).
    /// </summary>
    [TestFixture]
    public class BiquadLowPassFilterTests {

        const int SampleRate = 44100;

        static float Rms(float[] samples) {
            double sum = 0.0;
            foreach (float s in samples)
                sum += (double)s * s;
            return (float)Math.Sqrt(sum / samples.Length);
        }

        static float[] Tone(float frequency, int length, int sampleRate) {
            float[] buffer = new float[length];
            for (int i = 0; i < length; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);
            return buffer;
        }

        static float[] Filtered(float[] input, FilterParameters parameters, int sampleRate) {
            BiquadLowPassFilter filter = new BiquadLowPassFilter(parameters, sampleRate);
            float[] output = new float[input.Length];
            for (int i = 0; i < input.Length; i++)
                output[i] = filter.Process(input[i]);
            return output;
        }

        [Test]
        [Description("A cutoff well below a high tone attenuates it, while a low tone passes near unity.")]
        public void LowPass_AttenuatesHighFrequency_PassesLowFrequency() {
            FilterParameters parameters = new FilterParameters(500f, FilterParameters.ButterworthResonance);

            float[] lowTone = Tone(200f, SampleRate, SampleRate);
            float[] highTone = Tone(8000f, SampleRate, SampleRate);

            float lowRatio = Rms(Slice(Filtered(lowTone, parameters, SampleRate), 1000)) / Rms(Slice(lowTone, 1000));
            float highRatio = Rms(Slice(Filtered(highTone, parameters, SampleRate), 1000)) / Rms(Slice(highTone, 1000));

            Assert.That(lowRatio, Is.GreaterThan(0.7f), $"low tone below cutoff was over-attenuated; ratio={lowRatio}.");
            Assert.That(highRatio, Is.LessThan(0.1f), $"high tone above cutoff was not attenuated; ratio={highRatio}.");
            Assert.That(highRatio, Is.LessThan(lowRatio), "high frequency must be attenuated more than low frequency.");
        }

        [Test]
        [Description("An open filter returns every sample bit-for-bit, guaranteeing no regression on untouched patches.")]
        public void OpenFilter_IsExactPassthrough() {
            BiquadLowPassFilter filter = new BiquadLowPassFilter(FilterParameters.Default, SampleRate);

            float[] input = { 0f, 0.5f, -0.8f, 1f, -1f, 0.123456f, 0.999f, -0.333333f };
            foreach (float sample in input) {
                float output = filter.Process(sample);
                Assert.That(output, Is.EqualTo(sample), $"open filter altered sample {sample} to {output}.");
            }
        }

        [Test]
        [Description("A cutoff at or above the SF2 open sentinel is treated as open (passthrough) at any sample rate.")]
        public void CutoffAtOpenSentinel_IsPassthrough() {
            FilterParameters parameters = new FilterParameters(FilterParameters.Sf2OpenCutoffHz, 4f);
            BiquadLowPassFilter filter = new BiquadLowPassFilter(parameters, 48000);

            float[] input = { 0.2f, -0.7f, 0.42f, -0.9f };
            foreach (float sample in input)
                Assert.That(filter.Process(sample), Is.EqualTo(sample), "cutoff at the open sentinel must pass through.");
        }

        [Test]
        [Description("Extreme resonance and a near-Nyquist cutoff still yield finite output (the section never makes NaN/Inf).")]
        public void ExtremeParameters_ProduceFiniteOutput() {
            FilterParameters parameters = new FilterParameters(100f, 10000f);
            BiquadLowPassFilter filter = new BiquadLowPassFilter(parameters, SampleRate);

            float[] input = Tone(60f, 4000, SampleRate);
            foreach (float sample in input) {
                float output = filter.Process(sample);
                Assert.That(float.IsNaN(output), Is.False, "filter produced NaN.");
                Assert.That(float.IsInfinity(output), Is.False, "filter produced infinity.");
            }
        }

        [Test]
        [Description("A higher resonance produces a taller magnitude peak at the cutoff than a flat Butterworth response.")]
        public void HigherResonance_BoostsEnergyAtCutoff() {
            const float cutoff = 1000f;
            float[] toneAtCutoff = Tone(cutoff, SampleRate, SampleRate);

            float flat = Rms(Slice(
                Filtered(toneAtCutoff, new FilterParameters(cutoff, FilterParameters.ButterworthResonance), SampleRate), 2000));
            float resonant = Rms(Slice(
                Filtered(toneAtCutoff, new FilterParameters(cutoff, 8f), SampleRate), 2000));

            Assert.That(resonant, Is.GreaterThan(flat),
                $"resonant response at cutoff ({resonant}) should exceed the flat response ({flat}).");
        }

        static float[] Slice(float[] samples, int from) {
            float[] result = new float[samples.Length - from];
            Array.Copy(samples, from, result, 0, result.Length);
            return result;
        }

        [Test]
        [Description("SetCutoff preserves filter state across a retarget: feeding a constant before and after " +
            "the retarget produces no output jump (design §8: state1/state2 continuity, no reset click).")]
        public void SetCutoff_PreservesState_NoJumpOnConstantInput() {
            BiquadLowPassFilter filter = new BiquadLowPassFilter(
                new FilterParameters(2000f, FilterParameters.ButterworthResonance), SampleRate);

            float lastBeforeRetarget = 0f;
            for (int i = 0; i < 200; i++)
                lastBeforeRetarget = filter.Process(0.5f);

            filter.SetCutoff(600f);

            float firstAfterRetarget = filter.Process(0.5f);

            Assert.That(Math.Abs(firstAfterRetarget - lastBeforeRetarget), Is.LessThan(0.05f),
                $"SetCutoff introduced a jump: {lastBeforeRetarget} -> {firstAfterRetarget}.");
        }

        [Test]
        [Description("After SetCutoff the filter behaves at the new cutoff: a high tone that passed at the old " +
            "(high) cutoff is attenuated once swept to a low cutoff.")]
        public void SetCutoff_ChangesTransferFunction_ToNewCutoff() {
            BiquadLowPassFilter filter = new BiquadLowPassFilter(
                new FilterParameters(10000f, FilterParameters.ButterworthResonance), SampleRate);

            float[] tone = Tone(8000f, SampleRate, SampleRate);
            float[] beforeSweep = new float[2000];
            for (int i = 0; i < beforeSweep.Length; i++)
                beforeSweep[i] = filter.Process(tone[i]);

            filter.SetCutoff(300f);

            float[] afterSweep = new float[2000];
            for (int i = 0; i < afterSweep.Length; i++)
                afterSweep[i] = filter.Process(tone[beforeSweep.Length + i]);

            float rmsBefore = Rms(Slice(beforeSweep, 1000));
            float rmsAfter = Rms(Slice(afterSweep, 1000));

            Assert.That(rmsAfter, Is.LessThan(rmsBefore * 0.2f),
                $"sweeping to a low cutoff must attenuate the 8 kHz tone; before={rmsBefore}, after={rmsAfter}.");
        }

        [Test]
        [Description("SetCutoff clamps exactly as the constructor does, so finite output is preserved (INV-2) " +
            "even when swept to an extreme cutoff.")]
        public void SetCutoff_ExtremeCutoff_ProducesFiniteOutput() {
            BiquadLowPassFilter filter = new BiquadLowPassFilter(
                new FilterParameters(1000f, FilterParameters.ButterworthResonance), SampleRate);

            filter.SetCutoff(1_000_000f);

            float[] input = Tone(60f, 4000, SampleRate);
            foreach (float sample in input) {
                float output = filter.Process(sample);
                Assert.That(float.IsNaN(output), Is.False, "swept filter produced NaN.");
                Assert.That(float.IsInfinity(output), Is.False, "swept filter produced infinity.");
            }
        }
    }
}

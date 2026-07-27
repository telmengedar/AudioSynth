using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="Reverb"/>'s stability contract (DiVoid #7164 §8/§9): a bounded impulse
    /// must produce bounded, NaN/Inf-free, decaying output — proven directly against the DSP, not assumed
    /// from the <see cref="ReverbSettings"/> feedback clamp alone — and <see cref="ReverbSettings.Wet"/> = 0
    /// must be a structural, float-exact passthrough.
    /// </summary>
    [TestFixture]
    public class ReverbStabilityTests {

        const int SampleRate = 44100;
        const int WindowFrames = 4096;

        static float WindowRms(float[] block, int windowIndex, int windowFrames) {
            int start = windowIndex * windowFrames * 2;
            int count = windowFrames * 2;
            double sum = 0.0;
            for (int i = 0; i < count; i++) {
                float s = block[start + i];
                sum += (double)s * s;
            }
            return (float)Math.Sqrt(sum / count);
        }

        static float[] RenderImpulseResponse(ReverbSettings settings, int windows) {
            Reverb reverb = new Reverb(settings, SampleRate);
            float[] block = new float[WindowFrames * 2 * windows];
            block[0] = 1f;
            block[1] = 1f;
            reverb.Process(block, block);
            return block;
        }

        [Test]
        [Description("A bounded (unit) impulse produces bounded, NaN/Inf-free output across many subsequent " +
                     "windows: the reverb never relies on the master soft-clip or Finalize to stay in range.")]
        public void BoundedImpulse_ProducesBoundedNanInfFreeOutput() {
            float[] output = RenderImpulseResponse(
                new ReverbSettings(roomSize: 0.99f, damping: 0.0f, wet: 1f, width: 1f), windows: 20);

            foreach (float s in output) {
                Assert.That(float.IsNaN(s), Is.False, "reverb output must never be NaN.");
                Assert.That(float.IsInfinity(s), Is.False, "reverb output must never be infinite.");
                Assert.That(Math.Abs(s), Is.LessThan(2f), $"reverb output must stay bounded for a unit impulse; found {s}.");
            }
        }

        [Test]
        [Description("RoomSize is clamped to [0,1] regardless of caller input, so Feedback (RoomSize*0.28+0.7) " +
                     "always lands in [0.7, 0.98] and can never reach or exceed 1 (the BIBO stability precondition).")]
        public void RoomSize_OutOfRangeInput_ClampsFeedbackBelowOne() {
            ReverbSettings tooLarge = new ReverbSettings(roomSize: 5f);
            ReverbSettings tooSmall = new ReverbSettings(roomSize: -5f);

            Assert.That(tooLarge.Feedback, Is.EqualTo(0.98f).Within(1e-6f));
            Assert.That(tooLarge.Feedback, Is.LessThan(1f));
            Assert.That(tooSmall.Feedback, Is.EqualTo(0.7f).Within(1e-6f));
        }

        [Test]
        [Description("The impulse tail's RMS decays across successive windows: the reverb loses energy every " +
                     "pass instead of sustaining or growing it, which would indicate runaway feedback.")]
        public void ImpulseTail_DecaysAcrossSuccessiveWindows() {
            float[] output = RenderImpulseResponse(
                new ReverbSettings(roomSize: 0.99f, damping: 0.3f, wet: 1f, width: 1f), windows: 20);

            float earlyRms = WindowRms(output, windowIndex: 1, WindowFrames);
            float lateRms = WindowRms(output, windowIndex: 18, WindowFrames);

            Assert.That(earlyRms, Is.GreaterThan(0f), "expected a non-silent tail shortly after the impulse.");
            Assert.That(lateRms, Is.LessThan(earlyRms),
                $"expected the tail to decay over time; early RMS={earlyRms}, late RMS={lateRms}.");
        }

        [Test]
        [Description("With Wet = 0, Process must leave every sample unchanged: dry*1.0 + wet*0.0 is float-exact.")]
        public void WetZero_IsExactPassthrough() {
            ReverbSettings settings = new ReverbSettings(roomSize: 0.9f, damping: 0.5f, wet: 0f, width: 1f);
            Reverb reverb = new Reverb(settings, SampleRate);

            float[] block = new float[256];
            Random random = new Random(1234);
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            float[] original = (float[])block.Clone();

            reverb.Process(block, block);

            Assert.That(block, Is.EqualTo(original), "Wet=0 must reproduce the input bit-for-bit.");
        }
    }
}

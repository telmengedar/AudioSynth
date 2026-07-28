using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="Chorus"/>'s stability contract and <see cref="ChorusSettings"/>'s
    /// stability clamps (DiVoid #7188, design #7190): a bounded input must produce bounded, NaN/Inf-free
    /// output across many subsequent windows — the chorus has no feedback path, so this is a simpler
    /// guarantee than <see cref="Reverb"/>'s, proven directly rather than assumed — and
    /// <see cref="ChorusSettings.Wet"/> = 0 must be a structural, float-exact passthrough. Mirrors
    /// <see cref="ReverbStabilityTests"/>.
    /// </summary>
    [TestFixture]
    public class ChorusStabilityTests {

        const int SampleRate = 44100;

        [Test]
        [Description("A sustained full-scale input produces bounded, NaN/Inf-free output across many " +
                     "windows spanning several LFO cycles: the chorus never relies on the master soft-clip " +
                     "or Finalize to stay in range.")]
        public void SustainedFullScaleInput_ProducesBoundedNanInfFreeOutput() {
            Chorus chorus = new Chorus(new ChorusSettings(wet: 1f), SampleRate);
            int frames = SampleRate * 3;
            float[] send = new float[frames * 2];
            for (int i = 0; i < send.Length; i += 2) {
                send[i] = 1f;
                send[i + 1] = -1f;
            }
            float[] master = new float[send.Length];

            chorus.Process(send, master);

            foreach (float s in master) {
                Assert.That(float.IsNaN(s), Is.False, "chorus output must never be NaN.");
                Assert.That(float.IsInfinity(s), Is.False, "chorus output must never be infinite.");
                Assert.That(Math.Abs(s), Is.LessThan(2f), $"chorus output must stay bounded for a full-scale input; found {s}.");
            }
        }

        [Test]
        [Description("With Wet = 0, Process must leave master unchanged: nothing is added when wet is zero.")]
        public void WetZero_IsExactPassthrough() {
            Chorus chorus = new Chorus(new ChorusSettings(wet: 0f), SampleRate);

            float[] block = new float[256];
            Random random = new Random(1234);
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            float[] original = (float[])block.Clone();
            float[] master = new float[block.Length];

            chorus.Process(block, master);

            Assert.That(master, Is.EqualTo(new float[block.Length]),
                "Wet=0 must add nothing to master.");
            Assert.That(block, Is.EqualTo(original), "Wet=0 must never mutate the send buffer.");
        }

        [Test]
        [Description("BaseDelayMs at or below DepthMs is raised above it, so the modulated read position " +
                     "baseDelay + depth*sin(lfo) can never reach zero or go negative.")]
        public void BaseDelayAtOrBelowDepth_IsRaisedAboveDepth() {
            ChorusSettings equal = new ChorusSettings(depthMs: 10f, baseDelayMs: 10f);
            ChorusSettings below = new ChorusSettings(depthMs: 10f, baseDelayMs: 2f);

            Assert.That(equal.BaseDelayMs, Is.GreaterThan(equal.DepthMs),
                "BaseDelayMs equal to DepthMs must be raised strictly above it.");
            Assert.That(below.BaseDelayMs, Is.GreaterThan(below.DepthMs),
                "BaseDelayMs below DepthMs must be raised strictly above it.");
        }

        [Test]
        [Description("RateHz is clamped to a non-negative, bounded range regardless of caller input.")]
        public void RateHz_OutOfRangeInput_Clamps() {
            ChorusSettings tooLarge = new ChorusSettings(rateHz: 500f);
            ChorusSettings negative = new ChorusSettings(rateHz: -5f);

            Assert.That(tooLarge.RateHz, Is.EqualTo(20f));
            Assert.That(negative.RateHz, Is.EqualTo(0f));
        }

        [Test]
        [Description("Wet is clamped to [0,1] regardless of caller input.")]
        public void Wet_OutOfRangeInput_Clamps() {
            ChorusSettings tooLarge = new ChorusSettings(wet: 5f);
            ChorusSettings negative = new ChorusSettings(wet: -5f);

            Assert.That(tooLarge.Wet, Is.EqualTo(1f));
            Assert.That(negative.Wet, Is.EqualTo(0f));
        }

        [Test]
        [Description("VoiceCount is clamped to [1,4] regardless of caller input.")]
        public void VoiceCount_OutOfRangeInput_Clamps() {
            ChorusSettings tooLarge = new ChorusSettings(voiceCount: 99);
            ChorusSettings tooSmall = new ChorusSettings(voiceCount: 0);

            Assert.That(tooLarge.VoiceCount, Is.EqualTo(4));
            Assert.That(tooSmall.VoiceCount, Is.EqualTo(1));
        }

        [Test]
        [Description("Constructing a Chorus with null settings throws, mirroring Reverb.")]
        public void Constructor_NullSettings_Throws() {
            Assert.Throws<ArgumentNullException>(() => new Chorus(null!, SampleRate));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [Description("Constructing a Chorus with a non-positive sample rate throws, mirroring Reverb.")]
        public void Constructor_NonPositiveSampleRate_Throws(int sampleRate) {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Chorus(ChorusSettings.Default, sampleRate));
        }
    }
}

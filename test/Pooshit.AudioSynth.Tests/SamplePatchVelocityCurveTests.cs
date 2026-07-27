using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Tests for the concave <see cref="SamplePatch.VelocityToGain"/> velocity-to-gain characteristic
    /// (DiVoid #7139): endpoints at 0 and 127, the concavity proof point (velocity 64 well below the
    /// linear 0.5), and monotonicity across the velocity range.
    /// </summary>
    [TestFixture]
    public class SamplePatchVelocityCurveTests {

        [Test]
        [Description("Velocity 0 maps to silence and velocity 127 maps to unity gain.")]
        public void VelocityToGain_Endpoints_AreZeroAndUnity() {
            Assert.That(SamplePatch.VelocityToGain(0), Is.EqualTo(0f));
            Assert.That(SamplePatch.VelocityToGain(127), Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        [Description("The curve is concave: mid velocity 64 maps to noticeably less than the linear 0.5 " +
                     "(≈0.254 = (64/127)²), so soft notes are genuinely soft.")]
        public void VelocityToGain_MidVelocity_IsWellBelowLinearHalf() {
            float gain = SamplePatch.VelocityToGain(64);
            Assert.That(gain, Is.LessThan(0.5f), "a concave curve must attenuate mid velocity below the linear 0.5.");
            Assert.That(gain, Is.EqualTo(0.254f).Within(0.002f), "velocity 64 must map to (64/127)² ≈ 0.254.");
        }

        [Test]
        [Description("Gain is strictly non-decreasing across the whole velocity range.")]
        public void VelocityToGain_AcrossRange_IsMonotonic() {
            float previous = -1f;
            for (int velocity = 0; velocity <= 127; velocity++) {
                float gain = SamplePatch.VelocityToGain(velocity);
                Assert.That(gain, Is.GreaterThanOrEqualTo(previous), $"gain must not decrease at velocity {velocity}.");
                previous = gain;
            }
        }

        [Test]
        [Description("The curve is below the linear velocity/127 map everywhere except the endpoints, " +
                     "confirming it widens dynamics rather than reproducing the linear response.")]
        public void VelocityToGain_BelowLinear_ExceptEndpoints() {
            for (int velocity = 1; velocity < 127; velocity++) {
                float concave = SamplePatch.VelocityToGain(velocity);
                float linear = velocity / 127f;
                Assert.That(concave, Is.LessThan(linear), $"velocity {velocity} must be attenuated below the linear map.");
            }
        }
    }
}

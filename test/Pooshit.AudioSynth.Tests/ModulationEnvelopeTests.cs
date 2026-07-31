using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="ModulationEnvelope"/>: stage progression at the 64-frame control-tick
    /// granularity, and the design §9.1 proof that decay/release ramp linearly in the value domain
    /// (unlike <see cref="AmplitudeEnvelope"/>'s geometric shape).
    /// </summary>
    [TestFixture]
    public class ModulationEnvelopeTests {

        const int SampleRate = 44100;
        const int ControlRateFrames = 64;

        static ModulationEnvelopeParameters Params(
            float delay = 0f, float attack = 0.01f, float hold = 0f,
            float decay = 0f, float sustain = 1f, float release = 0.01f) =>
            new ModulationEnvelopeParameters(delay, attack, hold, decay, sustain, release);

        static float AdvanceTicks(ref ModulationEnvelope envelope, int ticks) {
            float value = 0f;
            for (int i = 0; i < ticks; i++)
                value = envelope.Advance(ControlRateFrames);
            return value;
        }

        [Test]
        [Description("Delay stage holds the value at zero before the attack begins.")]
        public void Delay_HoldsZeroBeforeAttack() {
            ModulationEnvelope envelope = new ModulationEnvelope(Params(delay: 0.01f, attack: 0.01f), SampleRate);

            float value = envelope.Advance(ControlRateFrames);

            Assert.That(value, Is.EqualTo(0f), "delay stage must hold zero.");
            Assert.That(envelope.Stage, Is.EqualTo(EnvelopeStage.Delay));
        }

        [Test]
        [Description("Attack rises from zero toward the peak (1.0) rather than jumping.")]
        public void Attack_RampsFromZero_NoOnsetJump() {
            ModulationEnvelope envelope = new ModulationEnvelope(Params(attack: 0.05f), SampleRate);

            float first = envelope.Advance(ControlRateFrames);
            float second = envelope.Advance(ControlRateFrames);

            Assert.That(first, Is.GreaterThan(0f).And.LessThan(0.2f), $"first tick jumped to {first}; expected a small ramp step.");
            Assert.That(second, Is.GreaterThan(first), "attack must rise tick over tick.");
        }

        [Test]
        [Description("Attack reaches the peak value (1.0) by the end of the attack time.")]
        public void Attack_ReachesPeak_ByEndOfAttackTime() {
            int attackFrames = (int)(0.02f * SampleRate);
            int ticks = attackFrames / ControlRateFrames + 1;
            ModulationEnvelope envelope = new ModulationEnvelope(Params(attack: 0.02f, hold: 1f), SampleRate);

            float value = AdvanceTicks(ref envelope, ticks);

            Assert.That(value, Is.EqualTo(1f).Within(0.02f), $"attack did not reach peak; value={value}.");
        }

        [Test]
        [Description("Decay falls from the peak toward the sustain level and settles there.")]
        public void Decay_SettlesAtSustainLevel() {
            ModulationEnvelope envelope = new ModulationEnvelope(
                Params(attack: 0.001f, hold: 0f, decay: 0.02f, sustain: 0.4f), SampleRate);

            float value = AdvanceTicks(ref envelope, SampleRate / ControlRateFrames / 5);

            Assert.That(value, Is.EqualTo(0.4f).Within(0.02f), $"decay did not settle at sustain; value={value}.");
            Assert.That(envelope.Stage, Is.EqualTo(EnvelopeStage.Sustain));
        }

        [Test]
        [Description("Design §9.1: decay ramps linearly (constant successive-value difference), not geometrically " +
            "(constant ratio) — the shape distinguishing ModulationEnvelope from AmplitudeEnvelope.")]
        public void Decay_IsLinear_ConstantDifferenceNotConstantRatio() {
            ModulationEnvelope envelope = new ModulationEnvelope(
                Params(attack: 0.001f, hold: 0f, decay: 0.2f, sustain: 0f), SampleRate);

            AdvanceTicks(ref envelope, 2);
            Assert.That(envelope.Stage, Is.EqualTo(EnvelopeStage.Decay), "setup must land in the decay stage.");

            const int step = 15;
            float v1 = AdvanceTicks(ref envelope, step);
            float v2 = AdvanceTicks(ref envelope, step);
            float v3 = AdvanceTicks(ref envelope, step);
            float v4 = AdvanceTicks(ref envelope, step);

            Assert.That(v4, Is.GreaterThan(0f), "samples must be taken while still decaying, above the sustain floor.");

            float d1 = v1 - v2;
            float d2 = v2 - v3;
            float d3 = v3 - v4;
            Assert.That(d2, Is.EqualTo(d1).Within(0.01f), $"successive decay differences must be constant (linear); d1={d1}, d2={d2}.");
            Assert.That(d3, Is.EqualTo(d1).Within(0.01f), $"successive decay differences must be constant (linear); d1={d1}, d3={d3}.");
        }

        [Test]
        [Description("Release fades linearly from the current value rather than jumping to zero.")]
        public void Release_FadesFromCurrentLevel_NoJump() {
            ModulationEnvelope envelope = new ModulationEnvelope(
                Params(attack: 0.001f, sustain: 1f, release: 0.05f), SampleRate);

            float atSustain = AdvanceTicks(ref envelope, SampleRate / ControlRateFrames / 20);

            envelope.Release();
            float firstReleaseTick = envelope.Advance(ControlRateFrames);

            Assert.That(firstReleaseTick, Is.LessThan(atSustain), "release must fade below the sustain level.");
            Assert.That(atSustain - firstReleaseTick, Is.LessThan(0.05f),
                $"first release tick dropped by {atSustain - firstReleaseTick}; expected a gentle fade, not a jump.");
        }

        [Test]
        [Description("Release drives the value to zero and reports IsFinished by the end of the release time.")]
        public void Release_ReachesZeroAndFinishes() {
            ModulationEnvelope envelope = new ModulationEnvelope(
                Params(attack: 0.001f, sustain: 1f, release: 0.02f), SampleRate);

            AdvanceTicks(ref envelope, 5);
            envelope.Release();

            float value = AdvanceTicks(ref envelope, SampleRate / ControlRateFrames);

            Assert.That(value, Is.EqualTo(0f), "release must reach zero.");
            Assert.That(envelope.IsFinished, Is.True, "envelope must report finished after the release tail.");
        }

        [Test]
        [Description("Release before the attack completes fades from the partial value, not from full scale.")]
        public void Release_MidAttack_FadesFromPartialLevel() {
            ModulationEnvelope envelope = new ModulationEnvelope(
                Params(attack: 0.2f, sustain: 1f, release: 0.05f), SampleRate);

            float partial = AdvanceTicks(ref envelope, 5);
            Assert.That(partial, Is.GreaterThan(0f).And.LessThan(1f), "should be mid-attack.");

            envelope.Release();
            float firstReleaseTick = envelope.Advance(ControlRateFrames);

            Assert.That(firstReleaseTick, Is.LessThan(partial), "mid-attack release must fade downward from the partial value.");
            Assert.That(firstReleaseTick, Is.GreaterThanOrEqualTo(0f), "mid-attack release must not go negative.");
        }

        [Test]
        [Description("Default parameters sustain at full value (1), matching EnvelopeParameters.Default's shape.")]
        public void Default_SustainsAtFullValue() {
            ModulationEnvelope envelope = new ModulationEnvelope(ModulationEnvelopeParameters.Default, SampleRate);

            float value = AdvanceTicks(ref envelope, 200);

            Assert.That(value, Is.EqualTo(1f).Within(1e-4f), "the default mod envelope must sustain at full value.");
        }

        [Test]
        [Description("Gen-29 sustain unit contract: sustainLevel = 1 - units/1000 is a decrease from full, " +
            "distinct from the volume envelope's centibel-attenuation sustain.")]
        public void SustainLevel_IsUnipolarDecreaseFromFull() {
            ModulationEnvelope envelope = new ModulationEnvelope(
                Params(attack: 0.001f, hold: 0f, decay: 0.001f, sustain: 0.7f), SampleRate);

            float value = AdvanceTicks(ref envelope, 50);

            Assert.That(value, Is.EqualTo(0.7f).Within(0.01f), "sustain level must settle at exactly the configured unipolar value.");
        }
    }
}

using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="AmplitudeEnvelope"/>: stage progression, and the deliverable proof
    /// that the envelope's boundary samples ramp rather than jump at onset and at release.
    /// </summary>
    [TestFixture]
    public class AmplitudeEnvelopeTests {

        const int SampleRate = 44100;

        static EnvelopeParameters Params(
            float delay = 0f, float attack = 0.01f, float hold = 0f,
            float decay = 0f, float sustain = 1f, float release = 0.01f) =>
            new EnvelopeParameters(delay, attack, hold, decay, sustain, release);

        static float AdvanceN(ref AmplitudeEnvelope envelope, int frames) {
            float level = 0f;
            for (int i = 0; i < frames; i++)
                level = envelope.AdvanceFrame();
            return level;
        }

        [Test]
        [Description("Attack onset ramps up in small steps from ~0 rather than jumping to full scale.")]
        public void Attack_RampsFromZero_NoOnsetJump() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(Params(attack: 0.01f), SampleRate);

            float first = envelope.AdvanceFrame();
            float second = envelope.AdvanceFrame();

            Assert.That(first, Is.LessThan(0.1f), $"first attack frame jumped to {first}; expected a small ramp step.");
            Assert.That(second, Is.GreaterThan(first), "attack must rise frame over frame.");
        }

        [Test]
        [Description("Attack reaches the peak level (1.0) by the end of the attack time.")]
        public void Attack_ReachesPeak_ByEndOfAttackTime() {
            int attackFrames = (int)(0.01f * SampleRate);
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(Params(attack: 0.01f, hold: 1f), SampleRate);

            float level = 0f;
            for (int i = 0; i < attackFrames; i++)
                level = envelope.AdvanceFrame();

            Assert.That(level, Is.EqualTo(1f).Within(0.02f), $"attack did not reach peak; level={level}.");
        }

        [Test]
        [Description("No consecutive-frame delta exceeds a small epsilon during attack (no zipper/jump).")]
        public void Attack_ConsecutiveDeltaBounded() {
            int attackFrames = (int)(0.01f * SampleRate);
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(Params(attack: 0.01f), SampleRate);

            float previous = 0f;
            float maxDelta = 0f;
            for (int i = 0; i < attackFrames; i++) {
                float level = envelope.AdvanceFrame();
                float delta = System.Math.Abs(level - previous);
                if (delta > maxDelta)
                    maxDelta = delta;
                previous = level;
            }

            Assert.That(maxDelta, Is.LessThan(0.01f),
                $"attack step {maxDelta} is too large for a 10 ms ramp; indicates a jump.");
        }

        [Test]
        [Description("Delay stage holds the level at zero before the attack begins.")]
        public void Delay_HoldsSilenceBeforeAttack() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(Params(delay: 0.01f, attack: 0.01f), SampleRate);

            float level = envelope.AdvanceFrame();

            Assert.That(level, Is.EqualTo(0f), "delay stage must hold silence.");
            Assert.That(envelope.Stage, Is.EqualTo(EnvelopeStage.Delay));
        }

        [Test]
        [Description("Decay falls from the peak toward the sustain level and settles there.")]
        public void Decay_SettlesAtSustainLevel() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(
                Params(attack: 0.001f, hold: 0f, decay: 0.01f, sustain: 0.5f), SampleRate);

            float level = 0f;
            for (int i = 0; i < SampleRate / 10; i++)
                level = envelope.AdvanceFrame();

            Assert.That(level, Is.EqualTo(0.5f).Within(0.02f), $"decay did not settle at sustain; level={level}.");
            Assert.That(envelope.Stage, Is.EqualTo(EnvelopeStage.Sustain));
        }

        [Test]
        [Description("Release fades from the current level rather than jumping to zero (declick proof).")]
        public void Release_FadesFromCurrentLevel_NoNoteOffJump() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(
                Params(attack: 0.001f, sustain: 1f, release: 0.05f), SampleRate);

            float atSustain = 0f;
            for (int i = 0; i < SampleRate / 20; i++)
                atSustain = envelope.AdvanceFrame();

            envelope.Release();
            float firstReleaseFrame = envelope.AdvanceFrame();

            Assert.That(firstReleaseFrame, Is.LessThan(atSustain),
                "release must fade below the sustain level.");
            Assert.That(atSustain - firstReleaseFrame, Is.LessThan(0.01f),
                $"first release frame dropped by {atSustain - firstReleaseFrame}; expected a gentle fade, not a jump.");
        }

        [Test]
        [Description("Release drives the level to zero and reports IsFinished by the end of the release time.")]
        public void Release_ReachesZeroAndFinishes() {
            int releaseFrames = (int)(0.02f * SampleRate);
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(
                Params(attack: 0.001f, sustain: 1f, release: 0.02f), SampleRate);

            for (int i = 0; i < 100; i++)
                envelope.AdvanceFrame();

            envelope.Release();
            float level = 1f;
            for (int i = 0; i < releaseFrames + 2; i++)
                level = envelope.AdvanceFrame();

            Assert.That(level, Is.EqualTo(0f), "release must reach zero.");
            Assert.That(envelope.IsFinished, Is.True, "envelope must report finished after the release tail.");
        }

        [Test]
        [Description("Release before the attack completes fades from the partial level, not from full scale.")]
        public void Release_MidAttack_FadesFromPartialLevel() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(
                Params(attack: 0.1f, sustain: 1f, release: 0.05f), SampleRate);

            float partial = 0f;
            for (int i = 0; i < 100; i++)
                partial = envelope.AdvanceFrame();

            Assert.That(partial, Is.GreaterThan(0f).And.LessThan(1f), "should be mid-attack.");

            envelope.Release();
            float firstReleaseFrame = envelope.AdvanceFrame();

            Assert.That(firstReleaseFrame, Is.LessThan(partial),
                "mid-attack release must fade downward from the partial level.");
            Assert.That(firstReleaseFrame, Is.GreaterThan(0f),
                "mid-attack release must not jump straight to zero.");
        }

        [Test]
        [Description("Release decays geometrically (constant successive-level ratio), not linearly (constant difference) — the exponential-shape proof for BUG #7184.")]
        public void Release_IsExponential_ConstantRatioNotConstantDifference() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(
                Params(attack: 0.001f, sustain: 1f, release: 0.2f), SampleRate);

            for (int i = 0; i < 200; i++)
                envelope.AdvanceFrame();

            envelope.Release();

            const int step = 1000;
            float l1 = AdvanceN(ref envelope, step);
            float l2 = AdvanceN(ref envelope, step);
            float l3 = AdvanceN(ref envelope, step);
            float l4 = AdvanceN(ref envelope, step);

            float r1 = l2 / l1;
            float r2 = l3 / l2;
            float r3 = l4 / l3;
            Assert.That(r2, Is.EqualTo(r1).Within(0.01f), $"successive release ratios must be constant; r1={r1}, r2={r2}.");
            Assert.That(r3, Is.EqualTo(r1).Within(0.01f), $"successive release ratios must be constant; r1={r1}, r3={r3}.");

            float d1 = l1 - l2;
            float d2 = l2 - l3;
            float d3 = l3 - l4;
            Assert.That(d2, Is.LessThan(d1), "exponential release: successive differences shrink; a linear fade would keep them equal.");
            Assert.That(d3, Is.LessThan(d2), "exponential release: successive differences shrink; a linear fade would keep them equal.");
        }

        [Test]
        [Description("Decay falls geometrically toward the sustain level (constant successive-level ratio), not linearly.")]
        public void Decay_IsExponential_ConstantRatio() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(
                Params(attack: 0.001f, hold: 0f, decay: 0.2f, sustain: 0.25f), SampleRate);

            AdvanceN(ref envelope, 100);
            Assert.That(envelope.Stage, Is.EqualTo(EnvelopeStage.Decay), "setup must land in the decay stage.");

            const int step = 1000;
            float l1 = AdvanceN(ref envelope, step);
            float l2 = AdvanceN(ref envelope, step);
            float l3 = AdvanceN(ref envelope, step);
            float l4 = AdvanceN(ref envelope, step);

            Assert.That(l4, Is.GreaterThan(0.25f), "samples must be taken while still decaying, above the sustain floor.");

            float r1 = l2 / l1;
            float r2 = l3 / l2;
            float r3 = l4 / l3;
            Assert.That(r2, Is.EqualTo(r1).Within(0.01f), $"successive decay ratios must be constant; r1={r1}, r2={r2}.");
            Assert.That(r3, Is.EqualTo(r1).Within(0.01f), $"successive decay ratios must be constant; r1={r1}, r3={r3}.");

            float d1 = l1 - l2;
            float d2 = l2 - l3;
            float d3 = l3 - l4;
            Assert.That(d2, Is.LessThan(d1), "exponential decay: successive differences shrink; a linear decay would keep them equal.");
            Assert.That(d3, Is.LessThan(d2), "exponential decay: successive differences shrink; a linear decay would keep them equal.");
        }

        [Test]
        [Description("Default parameters sustain at full level (SF2 default 0 cB), so note-on is non-decreasing.")]
        public void Default_SustainsAtFullLevel() {
            AmplitudeEnvelope envelope = new AmplitudeEnvelope(EnvelopeParameters.Default, SampleRate);

            float level = 0f;
            for (int i = 0; i < 1000; i++)
                level = envelope.AdvanceFrame();

            Assert.That(level, Is.EqualTo(1f).Within(1e-4f),
                "the default envelope must sustain at full level.");
        }
    }
}

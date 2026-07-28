using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Per-frame DAHDSR volume envelope: advances one frame at a time through delay, attack, hold,
    /// decay, sustain and release, producing the note's amplitude contour in the range [0, 1].  Attack
    /// is linear; decay and release advance geometrically (a fixed per-frame multiply), which is the
    /// SF2 linear-in-dB shape — notes damp naturally and clear to silence.  The advance is per-frame, so
    /// block size is never an input and the contour is free of block-boundary steps (INV-1).  It is a
    /// mutable struct advanced in place, like <see cref="GainRamp"/>; copying it by value loses the
    /// in-flight stage state.
    /// </summary>
    public struct AmplitudeEnvelope {

        /// <summary>
        /// Linear amplitude treated as silence (≈ −100 dB) for terminating the geometric decay and
        /// release: the level below which the remaining tail is inaudible and safe to snap to the target.
        /// </summary>
        const float SilenceFloorLinear = 1e-5f;

        readonly int delayFrames;
        readonly int attackFrames;
        readonly int holdFrames;
        readonly int decayFrames;
        readonly int releaseFrames;
        readonly float attackStep;
        readonly float decayFactor;
        readonly float releaseFactor;
        readonly float sustainLevel;

        EnvelopeStage stage;
        float level;
        int framesRemaining;

        /// <summary>
        /// Creates an <see cref="AmplitudeEnvelope"/> positioned at the start of its delay stage.
        /// </summary>
        /// <param name="parameters">the DAHDSR times (seconds) and sustain level for this note</param>
        /// <param name="sampleRate">output sample rate, used to convert stage seconds into frame counts</param>
        public AmplitudeEnvelope(EnvelopeParameters parameters, int sampleRate) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            delayFrames = FramesFromSeconds(parameters.DelaySeconds, sampleRate);
            attackFrames = FramesFromSeconds(parameters.AttackSeconds, sampleRate);
            holdFrames = FramesFromSeconds(parameters.HoldSeconds, sampleRate);
            decayFrames = FramesFromSeconds(parameters.DecaySeconds, sampleRate);
            releaseFrames = FramesFromSeconds(parameters.ReleaseSeconds, sampleRate);
            sustainLevel = Clamp01(parameters.SustainLevel);

            attackStep = attackFrames > 0 ? 1f / attackFrames : 0f;
            decayFactor = decayFrames > 0 ? GeometricStepFactor(Math.Max(sustainLevel, SilenceFloorLinear), decayFrames) : 0f;
            releaseFactor = releaseFrames > 0 ? GeometricStepFactor(SilenceFloorLinear, releaseFrames) : 0f;

            stage = EnvelopeStage.Delay;
            level = 0f;
            framesRemaining = 0;

            BeginStage(EnvelopeStage.Delay);
        }

        /// <summary>
        /// The stage the envelope is currently in.
        /// </summary>
        public EnvelopeStage Stage => stage;

        /// <summary>
        /// True once the release stage has driven the level to zero and the voice may deactivate.
        /// </summary>
        public bool IsFinished => stage == EnvelopeStage.Finished;

        /// <summary>
        /// Advances the envelope by one frame and returns the amplitude for that frame.
        /// </summary>
        public float AdvanceFrame() {
            switch (stage) {
                case EnvelopeStage.Delay:
                    if (--framesRemaining <= 0)
                        BeginStage(EnvelopeStage.Attack);
                    break;

                case EnvelopeStage.Attack:
                    level += attackStep;
                    if (level > 1f)
                        level = 1f;
                    if (--framesRemaining <= 0) {
                        level = 1f;
                        BeginStage(EnvelopeStage.Hold);
                    }
                    break;

                case EnvelopeStage.Hold:
                    if (--framesRemaining <= 0)
                        BeginStage(EnvelopeStage.Decay);
                    break;

                case EnvelopeStage.Decay:
                    level *= decayFactor;
                    if (level < sustainLevel)
                        level = sustainLevel;
                    if (--framesRemaining <= 0) {
                        level = sustainLevel;
                        BeginStage(EnvelopeStage.Sustain);
                    }
                    break;

                case EnvelopeStage.Release:
                    level *= releaseFactor;
                    if (--framesRemaining <= 0) {
                        level = 0f;
                        BeginStage(EnvelopeStage.Finished);
                    }
                    break;
            }

            return level;
        }

        /// <summary>
        /// Enters the release stage from the current level; a note released mid-attack fades from
        /// wherever it was rather than jumping.  Has no effect once already releasing or finished.
        /// </summary>
        public void Release() {
            if (stage == EnvelopeStage.Release || stage == EnvelopeStage.Finished)
                return;
            BeginStage(EnvelopeStage.Release);
        }

        void BeginStage(EnvelopeStage next) {
            switch (next) {
                case EnvelopeStage.Delay:
                    stage = EnvelopeStage.Delay;
                    level = 0f;
                    framesRemaining = delayFrames;
                    if (delayFrames <= 0)
                        BeginStage(EnvelopeStage.Attack);
                    break;

                case EnvelopeStage.Attack:
                    stage = EnvelopeStage.Attack;
                    framesRemaining = attackFrames;
                    if (attackFrames <= 0) {
                        level = 1f;
                        BeginStage(EnvelopeStage.Hold);
                    }
                    break;

                case EnvelopeStage.Hold:
                    stage = EnvelopeStage.Hold;
                    level = 1f;
                    framesRemaining = holdFrames;
                    if (holdFrames <= 0)
                        BeginStage(EnvelopeStage.Decay);
                    break;

                case EnvelopeStage.Decay:
                    stage = EnvelopeStage.Decay;
                    framesRemaining = decayFrames;
                    if (decayFrames <= 0) {
                        level = sustainLevel;
                        BeginStage(EnvelopeStage.Sustain);
                    }
                    break;

                case EnvelopeStage.Sustain:
                    stage = EnvelopeStage.Sustain;
                    level = sustainLevel;
                    framesRemaining = 0;
                    break;

                case EnvelopeStage.Release:
                    stage = EnvelopeStage.Release;
                    framesRemaining = releaseFrames;
                    if (releaseFrames <= 0) {
                        level = 0f;
                        BeginStage(EnvelopeStage.Finished);
                    }
                    break;

                case EnvelopeStage.Finished:
                    stage = EnvelopeStage.Finished;
                    level = 0f;
                    framesRemaining = 0;
                    break;
            }
        }

        /// <summary>
        /// Per-frame multiplier that carries a level to <paramref name="targetRatio"/> times its start
        /// over <paramref name="frames"/> frames.  A constant multiplicative step is a constant
        /// dB-per-frame step, giving the SF2 linear-in-dB (exponential) decay and release shape.
        /// </summary>
        static float GeometricStepFactor(float targetRatio, int frames) {
            return (float)Math.Pow(targetRatio, 1.0 / frames);
        }

        static int FramesFromSeconds(float seconds, int sampleRate) {
            if (seconds <= 0f)
                return 0;
            double frames = Math.Round((double)seconds * sampleRate);
            if (frames < 0d)
                return 0;
            return (int)frames;
        }

        static float Clamp01(float value) {
            if (value < 0f)
                return 0f;
            if (value > 1f)
                return 1f;
            return value;
        }
    }
}

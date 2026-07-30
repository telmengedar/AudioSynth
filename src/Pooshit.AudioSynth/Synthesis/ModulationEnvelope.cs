using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Control-rate DADSR modulation envelope: advances in caller-supplied frame batches (one call per
    /// control tick, mirroring <see cref="ModulationLfo.Advance"/>) through delay, attack, hold, decay,
    /// sustain and release, producing a unipolar value in [0, 1]. Unlike <see cref="AmplitudeEnvelope"/>,
    /// decay and release ramp <b>linearly</b> in the value domain (design §9.1): the output already feeds
    /// a log-frequency (cents) domain via <see cref="FilterParameters.ModEnvToCutoffCents"/>, so a linear
    /// value ramp gives the musically-correct linear-in-cents cutoff sweep. It is a mutable struct
    /// advanced in place, like <see cref="AmplitudeEnvelope"/>; copying it by value loses the in-flight
    /// stage state. Gen-7 (mod-env-to-pitch) is not wired to this envelope's output; that is a deferred
    /// fast-follow, left as a clean seam (the caller already has the per-tick value available).
    /// </summary>
    public struct ModulationEnvelope {

        readonly int delayFrames;
        readonly int attackFrames;
        readonly int holdFrames;
        readonly int decayFrames;
        readonly int releaseFrames;
        readonly float attackStep;
        readonly float decayStep;
        readonly float sustainLevel;

        EnvelopeStage stage;
        float level;
        int framesRemaining;
        float releaseStep;

        /// <summary>
        /// Creates a <see cref="ModulationEnvelope"/> positioned at the start of its delay stage.
        /// </summary>
        /// <param name="parameters">the DADSR times (seconds) and sustain level for this note</param>
        /// <param name="sampleRate">output sample rate, used to convert stage seconds into frame counts</param>
        public ModulationEnvelope(ModulationEnvelopeParameters parameters, int sampleRate) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            delayFrames = FramesFromSeconds(parameters.DelaySeconds, sampleRate);
            attackFrames = FramesFromSeconds(parameters.AttackSeconds, sampleRate);
            holdFrames = FramesFromSeconds(parameters.HoldSeconds, sampleRate);
            decayFrames = FramesFromSeconds(parameters.DecaySeconds, sampleRate);
            releaseFrames = FramesFromSeconds(parameters.ReleaseSeconds, sampleRate);
            sustainLevel = Clamp01(parameters.SustainLevel);

            attackStep = attackFrames > 0 ? 1f / attackFrames : 0f;
            decayStep = decayFrames > 0 ? (1f - sustainLevel) / decayFrames : 0f;
            releaseStep = 0f;

            stage = EnvelopeStage.Delay;
            level = 0f;
            framesRemaining = 0;

            BeginStage(EnvelopeStage.Delay);
        }

        /// <summary>The stage the envelope is currently in.</summary>
        public EnvelopeStage Stage => stage;

        /// <summary>True once the release stage has driven the level to zero.</summary>
        public bool IsFinished => stage == EnvelopeStage.Finished;

        /// <summary>
        /// Advances the envelope by <paramref name="frames"/> output frames (a control tick) and returns
        /// the unipolar value, in [0, 1], after the advance.
        /// </summary>
        /// <param name="frames">number of output frames elapsed since the previous call</param>
        public float Advance(int frames) {
            while (frames > 0 && stage != EnvelopeStage.Finished) {
                switch (stage) {
                    case EnvelopeStage.Delay: {
                        int consumed = Math.Min(framesRemaining, frames);
                        framesRemaining -= consumed;
                        frames -= consumed;
                        if (framesRemaining <= 0)
                            BeginStage(EnvelopeStage.Attack);
                        break;
                    }

                    case EnvelopeStage.Attack: {
                        int consumed = Math.Min(framesRemaining, frames);
                        level += attackStep * consumed;
                        if (level > 1f)
                            level = 1f;
                        framesRemaining -= consumed;
                        frames -= consumed;
                        if (framesRemaining <= 0) {
                            level = 1f;
                            BeginStage(EnvelopeStage.Hold);
                        }
                        break;
                    }

                    case EnvelopeStage.Hold: {
                        int consumed = Math.Min(framesRemaining, frames);
                        framesRemaining -= consumed;
                        frames -= consumed;
                        if (framesRemaining <= 0)
                            BeginStage(EnvelopeStage.Decay);
                        break;
                    }

                    case EnvelopeStage.Decay: {
                        int consumed = Math.Min(framesRemaining, frames);
                        level -= decayStep * consumed;
                        if (level < sustainLevel)
                            level = sustainLevel;
                        framesRemaining -= consumed;
                        frames -= consumed;
                        if (framesRemaining <= 0) {
                            level = sustainLevel;
                            BeginStage(EnvelopeStage.Sustain);
                        }
                        break;
                    }

                    case EnvelopeStage.Sustain:
                        frames = 0;
                        break;

                    case EnvelopeStage.Release: {
                        int consumed = Math.Min(framesRemaining, frames);
                        level -= releaseStep * consumed;
                        if (level < 0f)
                            level = 0f;
                        framesRemaining -= consumed;
                        frames -= consumed;
                        if (framesRemaining <= 0) {
                            level = 0f;
                            BeginStage(EnvelopeStage.Finished);
                        }
                        break;
                    }
                }
            }

            return level;
        }

        /// <summary>
        /// Enters the release stage from the current level, ramping linearly to zero; a note released
        /// mid-attack or mid-decay fades from wherever it was rather than jumping. Has no effect once
        /// already releasing or finished.
        /// </summary>
        public void Release() {
            if (stage == EnvelopeStage.Release || stage == EnvelopeStage.Finished)
                return;
            releaseStep = releaseFrames > 0 ? level / releaseFrames : 0f;
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

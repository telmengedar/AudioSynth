using System;

namespace Pooshit.AudioSynth.Synthesis.Voices {

    /// <summary>
    /// <see cref="IVoice"/> that reads a mono <see cref="SampleRegion"/> at a pitch-derived increment with
    /// linear interpolation and renders a mono block, applying the region's <see cref="BiquadLowPassFilter"/>
    /// then its <see cref="AmplitudeEnvelope"/> and a <see cref="GainRamp"/>. Each control tick recomputes
    /// pitch (LFO, pitch-bend, mod-wheel) and the effective filter cutoff (base + LFO + mod-envelope,
    /// combined in cents); zero depth on every routing reproduces the pre-feature render bit-for-bit.
    /// </summary>
    public sealed class SamplePlaybackVoice : IVoice {

        const int ControlRateFrames = 64;

        /// <summary>
        /// Peak mod-wheel vibrato depth, in cents, at full LFO excursion and amount=1 (GM/DLS
        /// default-modulator convention: CC1 maps to a ±50-cent vibrato).
        /// </summary>
        const float MaxModWheelVibratoCents = 50f;

        const float CutoffEpsilonCents = 0.5f;

        readonly SampleRegion region;
        readonly float pitchIncrement;
        readonly float baseCutoffCents;
        GainRamp gainRamp;
        AmplitudeEnvelope envelope;
        ModulationEnvelope modEnv;
        BiquadLowPassFilter filter;
        ModulationLfo lfo;
        ModulationLfo modWheelLfo;
        float effectiveIncrement;
        float bendFactor;
        float modWheelAmount;
        float tremoloCurrent;
        float tremoloStep;
        float lastAppliedCutoffCents;
        float frameGain;
        int controlTicksRemaining;
        double readPos;
        bool isActive;
        bool released;
        bool sampleExhausted;
        bool stealing;

        /// <summary>
        /// Creates a <see cref="SamplePlaybackVoice"/> using the region's own (key/velocity-independent)
        /// base cutoff and modulation-envelope parameters. Used by hand-built patches and tests that do not
        /// resolve per-note filter/mod-envelope values; <see cref="Patches.SamplePatch.StartVoice"/> uses
        /// the other constructor instead, since it always resolves both per note.
        /// </summary>
        /// <param name="region">the sample region to play</param>
        /// <param name="pitchIncrement">fractional read-position advance per output frame</param>
        /// <param name="targetGain">velocity-derived gain target the ramp converges toward from zero</param>
        /// <param name="outputSampleRate">engine output sample rate; determines the gain-ramp slew speed</param>
        public SamplePlaybackVoice(SampleRegion region, float pitchIncrement, float targetGain, int outputSampleRate)
            : this(region, pitchIncrement, targetGain, outputSampleRate,
                  (region ?? throw new ArgumentNullException(nameof(region))).Filter.BaseCutoffCents,
                  (region ?? throw new ArgumentNullException(nameof(region))).ModEnv) {
        }

        /// <summary>
        /// Creates a <see cref="SamplePlaybackVoice"/> with the per-note-resolved base cutoff (region base
        /// plus the velocity-to-filter-cutoff offset, SF2 §8.4.2) and modulation-envelope parameters (hold/decay
        /// resolved for the played key), as produced by <see cref="Patches.SamplePatch.StartVoice"/>.
        /// </summary>
        /// <param name="region">the sample region to play</param>
        /// <param name="pitchIncrement">fractional read-position advance per output frame</param>
        /// <param name="targetGain">velocity-derived gain target the ramp converges toward from zero</param>
        /// <param name="outputSampleRate">engine output sample rate; determines the gain-ramp slew speed</param>
        /// <param name="effectiveBaseCutoffCents">per-note base cutoff in absolute cents (region base + velocity offset)</param>
        /// <param name="modEnvParameters">per-note modulation-envelope parameters (hold/decay resolved for the played key)</param>
        public SamplePlaybackVoice(
            SampleRegion region, float pitchIncrement, float targetGain, int outputSampleRate,
            float effectiveBaseCutoffCents, ModulationEnvelopeParameters modEnvParameters) {
            this.region = region ?? throw new ArgumentNullException(nameof(region));
            this.pitchIncrement = pitchIncrement;
            baseCutoffCents = effectiveBaseCutoffCents;
            gainRamp = new GainRamp(outputSampleRate);
            gainRamp.SetTarget(targetGain);
            envelope = new AmplitudeEnvelope(region.Envelope, outputSampleRate);
            modEnv = new ModulationEnvelope(modEnvParameters, outputSampleRate);
            filter = new BiquadLowPassFilter(region.Filter, outputSampleRate);
            lfo = new ModulationLfo(region.Lfo, outputSampleRate);
            modWheelLfo = new ModulationLfo(
                new LfoParameters(0f, LfoParameters.Sf2DefaultFrequencyHz, MaxModWheelVibratoCents, 0f, 0f),
                outputSampleRate);
            effectiveIncrement = pitchIncrement;
            bendFactor = 1f;
            modWheelAmount = 0f;
            tremoloCurrent = 1f;
            tremoloStep = 0f;
            // Region's own base, not effectiveBaseCutoffCents, so an unchanged tick 0 skips SetCutoff entirely.
            lastAppliedCutoffCents = region.Filter.BaseCutoffCents;
            frameGain = 0f;
            controlTicksRemaining = 0;
            readPos = region.Start;
            isActive = true;
            released = false;
            sampleExhausted = false;
            stealing = false;
        }

        /// <inheritdoc/>
        public bool IsActive => isActive;

        /// <inheritdoc/>
        public void Release() {
            released = true;
            envelope.Release();
            modEnv.Release();
        }

        /// <inheritdoc/>
        public float CurrentGain => isActive ? frameGain : 0f;

        /// <inheritdoc/>
        public void FastFadeForSteal() {
            if (!isActive || stealing)
                return;
            stealing = true;
            gainRamp.SetTarget(0f);
        }

        /// <inheritdoc/>
        public void SetPitchBend(float pitchFactor) {
            bendFactor = pitchFactor;
        }

        /// <inheritdoc/>
        public void SetModWheel(float amount) {
            modWheelAmount = amount;
        }

        /// <inheritdoc/>
        public float Pan => region.Pan;

        /// <inheritdoc/>
        public float ReverbSend => region.ReverbSend;

        /// <inheritdoc/>
        public float ChorusSend => region.ChorusSend;

        /// <inheritdoc/>
        public int ExclusiveClass => region.ExclusiveClass;

        /// <inheritdoc/>
        public int RenderBlock(Span<float> block) {
            if (!isActive) {
                block.Clear();
                return block.Length;
            }

            int count = block.Length;
            for (int i = 0; i < count; i++) {
                if (controlTicksRemaining <= 0) {
                    float lfoValue = lfo.Advance(ControlRateFrames);
                    float regionVibrato = (float)Math.Pow(2.0, lfoValue * region.Lfo.PitchDepthCents / 1200.0);

                    float modVibrato;
                    if (modWheelAmount != 0f) {
                        float modLfoValue = modWheelLfo.Advance(ControlRateFrames);
                        modVibrato = (float)Math.Pow(2.0, modLfoValue * MaxModWheelVibratoCents * modWheelAmount / 1200.0);
                    } else {
                        modVibrato = 1f;
                    }

                    effectiveIncrement = pitchIncrement * regionVibrato * modVibrato * bendFactor;

                    if (region.Lfo.VolumeDepthCentibels != 0f) {
                        float tremoloTarget = (float)Math.Pow(10.0, lfoValue * region.Lfo.VolumeDepthCentibels / 200.0);
                        tremoloStep = (tremoloTarget - tremoloCurrent) / ControlRateFrames;
                    } else {
                        tremoloStep = 0f;
                        tremoloCurrent = 1f;
                    }

                    float modEnvValue = modEnv.Advance(ControlRateFrames);
                    float effectiveCutoffCents = baseCutoffCents
                        + lfoValue * region.Lfo.FilterDepthCents
                        + modEnvValue * region.Filter.ModEnvToCutoffCents;
                    if (Math.Abs(effectiveCutoffCents - lastAppliedCutoffCents) >= CutoffEpsilonCents) {
                        filter.SetCutoff(FilterParameters.CentsToHz(effectiveCutoffCents));
                        lastAppliedCutoffCents = effectiveCutoffCents;
                    }

                    controlTicksRemaining = ControlRateFrames;
                }
                controlTicksRemaining--;

                tremoloCurrent += tremoloStep;
                float gain = envelope.AdvanceFrame() * gainRamp.AdvanceFrame() * tremoloCurrent;
                frameGain = gain;

                float sample;
                if (sampleExhausted) {
                    sample = 0f;
                } else {
                    sample = ReadInterpolated();
                    AdvanceReadPosition();
                }

                sample = filter.Process(sample);

                block[i] = sample * gain;

                if (released && envelope.IsFinished) {
                    for (int j = i + 1; j < count; j++)
                        block[j] = 0f;
                    isActive = false;
                    return count;
                }

                if (stealing && gainRamp.IsAtTarget) {
                    for (int j = i + 1; j < count; j++)
                        block[j] = 0f;
                    isActive = false;
                    return count;
                }
            }

            if (sampleExhausted && !released)
                isActive = false;

            return count;
        }

        float ReadInterpolated() {
            float[] buf = region.Buffer;
            int regionEnd = region.End;

            int n = (int)readPos;
            float frac = (float)(readPos - n);

            float s0 = (n >= region.Start && n < regionEnd) ? buf[n] : 0f;
            int n1 = n + 1;
            float s1 = (n1 >= region.Start && n1 < regionEnd) ? buf[n1] : 0f;

            return s0 + frac * (s1 - s0);
        }

        void AdvanceReadPosition() {
            readPos += effectiveIncrement;

            if (region.LoopMode == LoopMode.Continuous) {
                double loopLen = region.LoopEnd - region.LoopStart;
                if (readPos >= region.LoopEnd) {
                    double excess = readPos - region.LoopStart;
                    readPos = region.LoopStart + (excess % loopLen);
                }
            } else {
                if (readPos >= region.End)
                    sampleExhausted = true;
            }
        }
    }
}

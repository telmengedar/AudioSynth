using System;

namespace Pooshit.AudioSynth.Synthesis.Voices {

    /// <summary>
    /// <see cref="IVoice"/> that reads a mono <see cref="SampleRegion"/> at a pitch-derived increment
    /// with linear interpolation and renders a mono block.  Each interpolated sample passes through the
    /// region's resonant <see cref="BiquadLowPassFilter"/> (the timbre-shaping stage) before being scaled
    /// by the product of the region's DAHDSR <see cref="AmplitudeEnvelope"/> (the note's amplitude
    /// contour, which owns the click-free onset and the note-off release fade) and a <see cref="GainRamp"/>
    /// (the zipper-free slew of the velocity-derived scalar gain).  The filter sits before the amplifier,
    /// realising the SF2 signal chain oscillator → low-pass filter → amplifier.  Supports no-loop one-shot
    /// and continuous looping.  The region's <see cref="ModulationLfo"/> re-evaluates every
    /// <see cref="ControlRateFrames"/> frames and steps the read-position increment's slope (vibrato);
    /// held-then-stepped is exactly how the increment already behaved before the LFO, so a tick never
    /// introduces an amplitude discontinuity (INV-1 by construction) and zero pitch depth reproduces the
    /// pre-LFO increment bit-for-bit.  Tremolo (LFO to volume) glides its multiplier linearly across each
    /// control block, since a stepped gain multiplier would itself be an audible click; filter-sweep (LFO
    /// to cutoff) re-targets the biquad at the control rate via <see cref="BiquadLowPassFilter.SetCutoff"/>,
    /// which is click-free by construction (state is preserved across the retarget).  Zero depth on all
    /// three routings reproduces the pre-LFO render bit-for-bit.  <see cref="SetPitchBend"/> folds a
    /// channel-driven pitch-bend ratio into the same control-tick increment recompute; a centered bend
    /// (1.0) reproduces the pre-bend increment bit-for-bit.
    /// </summary>
    public sealed class SamplePlaybackVoice : IVoice {

        const int ControlRateFrames = 64;

        readonly SampleRegion region;
        readonly float pitchIncrement;
        readonly float baseCutoffHz;
        GainRamp gainRamp;
        AmplitudeEnvelope envelope;
        BiquadLowPassFilter filter;
        ModulationLfo lfo;
        float effectiveIncrement;
        float bendFactor;
        float tremoloCurrent;
        float tremoloStep;
        int controlTicksRemaining;
        double readPos;
        bool isActive;
        bool released;
        bool sampleExhausted;

        /// <summary>
        /// Creates a <see cref="SamplePlaybackVoice"/>.
        /// </summary>
        /// <param name="region">the sample region to play</param>
        /// <param name="pitchIncrement">fractional read-position advance per output frame</param>
        /// <param name="targetGain">velocity-derived gain target the ramp converges toward from zero</param>
        /// <param name="outputSampleRate">engine output sample rate; determines the gain-ramp slew speed</param>
        public SamplePlaybackVoice(SampleRegion region, float pitchIncrement, float targetGain, int outputSampleRate) {
            this.region = region ?? throw new ArgumentNullException(nameof(region));
            this.pitchIncrement = pitchIncrement;
            baseCutoffHz = region.Filter.CutoffHz;
            gainRamp = new GainRamp(outputSampleRate);
            gainRamp.SetTarget(targetGain);
            envelope = new AmplitudeEnvelope(region.Envelope, outputSampleRate);
            filter = new BiquadLowPassFilter(region.Filter, outputSampleRate);
            lfo = new ModulationLfo(region.Lfo, outputSampleRate);
            effectiveIncrement = pitchIncrement;
            bendFactor = 1f;
            tremoloCurrent = 1f;
            tremoloStep = 0f;
            controlTicksRemaining = 0;
            readPos = region.Start;
            isActive = true;
            released = false;
            sampleExhausted = false;
        }

        /// <inheritdoc/>
        public bool IsActive => isActive;

        /// <inheritdoc/>
        public void Release() {
            released = true;
            envelope.Release();
        }

        /// <inheritdoc/>
        public void SetPitchBend(float pitchFactor) {
            bendFactor = pitchFactor;
        }

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
                    effectiveIncrement = pitchIncrement * (float)Math.Pow(2.0, lfoValue * region.Lfo.PitchDepthCents / 1200.0) * bendFactor;

                    if (region.Lfo.VolumeDepthCentibels != 0f) {
                        float tremoloTarget = (float)Math.Pow(10.0, lfoValue * region.Lfo.VolumeDepthCentibels / 200.0);
                        tremoloStep = (tremoloTarget - tremoloCurrent) / ControlRateFrames;
                    } else {
                        tremoloStep = 0f;
                        tremoloCurrent = 1f;
                    }

                    if (region.Lfo.FilterDepthCents != 0f) {
                        float effectiveCutoff = baseCutoffHz * (float)Math.Pow(2.0, lfoValue * region.Lfo.FilterDepthCents / 1200.0);
                        filter.SetCutoff(effectiveCutoff);
                    }

                    controlTicksRemaining = ControlRateFrames;
                }
                controlTicksRemaining--;

                tremoloCurrent += tremoloStep;
                float gain = envelope.AdvanceFrame() * gainRamp.AdvanceFrame() * tremoloCurrent;

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

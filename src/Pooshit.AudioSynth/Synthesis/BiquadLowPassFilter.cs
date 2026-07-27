using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Per-voice resonant low-pass filter: a single RBJ Audio-EQ-Cookbook low-pass biquad section in
    /// Transposed Direct Form II, whose coefficients are computed at note start from a
    /// <see cref="FilterParameters"/> descriptor and the output sample rate, then applied one sample at a
    /// time.  <see cref="SetCutoff"/> lets a caller re-target the cutoff later in the note (filter-sweep),
    /// recomputing coefficients while preserving filter state so the sweep is click-free.  It is a mutable
    /// struct advanced in place, like <see cref="AmplitudeEnvelope"/> and <see cref="GainRamp"/>; copying
    /// it by value loses the in-flight filter state.  <see cref="Process"/> is a single-sample multiply-add
    /// step, so block size is never an input (INV-1); coefficient recomputation is driven only at the
    /// caller's control rate, so the per-sample hot path stays allocation- and transcendental-free.  An
    /// open cutoff is realised as an exact passthrough, and cutoff and resonance are clamped so the
    /// coefficients are always finite and the section never itself produces NaN or infinity (INV-2
    /// support; <see cref="Synthesizer"/> remains the final choke point).
    /// </summary>
    public struct BiquadLowPassFilter {

        const float MinCutoffHz = 20f;
        const float MaxCutoffFractionOfSampleRate = 0.45f;
        const float MinResonance = 0.5f;
        const float MaxResonance = 25f;

        readonly int sampleRate;
        readonly float q;

        bool bypass;
        float b0;
        float b1;
        float b2;
        float a1;
        float a2;

        float state1;
        float state2;

        /// <summary>
        /// Creates a <see cref="BiquadLowPassFilter"/>, computing its coefficients once from the descriptor
        /// and sample rate.  A cutoff at or above <see cref="FilterParameters.Sf2OpenCutoffHz"/> yields a
        /// passthrough; otherwise the cutoff is clamped below the Nyquist frequency and the resonance to a
        /// stable band before the low-pass coefficients are formed.
        /// </summary>
        /// <param name="parameters">the rate-independent cutoff and resonance for this note</param>
        /// <param name="sampleRate">output sample rate the filter runs at, in frames per second</param>
        public BiquadLowPassFilter(FilterParameters parameters, int sampleRate) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            this.sampleRate = sampleRate;
            q = Clamp(parameters.Resonance, MinResonance, MaxResonance);
            state1 = 0f;
            state2 = 0f;
            bypass = false;
            b0 = 0f;
            b1 = 0f;
            b2 = 0f;
            a1 = 0f;
            a2 = 0f;

            Recompute(parameters.CutoffHz);
        }

        /// <summary>
        /// Filters one input sample and advances the filter state by one sample.  When the filter is open
        /// the input is returned unchanged, bit for bit.
        /// </summary>
        /// <param name="sample">the input sample to filter</param>
        /// <returns>the filtered sample</returns>
        public float Process(float sample) {
            if (bypass)
                return sample;

            float output = b0 * sample + state1;
            state1 = b1 * sample - a1 * output + state2;
            state2 = b2 * sample - a2 * output;
            return output;
        }

        /// <summary>
        /// Re-targets the cutoff to <paramref name="cutoffHz"/>, recomputing coefficients in place while
        /// preserving <c>state1</c>/<c>state2</c> so the retarget introduces no reset click.  Clamped
        /// exactly as the constructor clamps, so the recomputed coefficients stay finite (INV-2).
        /// </summary>
        /// <param name="cutoffHz">the new low-pass corner frequency in hertz</param>
        public void SetCutoff(float cutoffHz) {
            Recompute(cutoffHz);
        }

        void Recompute(float cutoffHz) {
            if (cutoffHz >= FilterParameters.Sf2OpenCutoffHz) {
                bypass = true;
                b0 = 1f;
                b1 = 0f;
                b2 = 0f;
                a1 = 0f;
                a2 = 0f;
                return;
            }

            bypass = false;

            float maxCutoff = MaxCutoffFractionOfSampleRate * sampleRate;
            float cutoff = Clamp(cutoffHz, MinCutoffHz, maxCutoff);

            double w0 = 2.0 * Math.PI * cutoff / sampleRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            double alpha = sinW0 / (2.0 * q);

            double normB0 = (1.0 - cosW0) / 2.0;
            double normB1 = 1.0 - cosW0;
            double normB2 = (1.0 - cosW0) / 2.0;
            double normA0 = 1.0 + alpha;
            double normA1 = -2.0 * cosW0;
            double normA2 = 1.0 - alpha;

            b0 = (float)(normB0 / normA0);
            b1 = (float)(normB1 / normA0);
            b2 = (float)(normB2 / normA0);
            a1 = (float)(normA1 / normA0);
            a2 = (float)(normA2 / normA0);
        }

        static float Clamp(float value, float min, float max) {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}

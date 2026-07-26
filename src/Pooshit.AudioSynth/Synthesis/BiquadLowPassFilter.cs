using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Per-voice static resonant low-pass filter: a single RBJ Audio-EQ-Cookbook low-pass biquad section
    /// whose coefficients are computed once at note start from a <see cref="FilterParameters"/> descriptor
    /// and the output sample rate, then applied one sample at a time.  It is a mutable struct advanced in
    /// place, like <see cref="AmplitudeEnvelope"/> and <see cref="GainRamp"/>; copying it by value loses
    /// the in-flight filter state.  <see cref="Process"/> is a single-sample multiply-add step, so block
    /// size is never an input (INV-1); the only transcendental math runs once in the constructor, so the
    /// per-sample hot path stays allocation- and transcendental-free.  An open cutoff is realised as an
    /// exact passthrough, and cutoff and resonance are clamped so the coefficients are always finite and
    /// the section never itself produces NaN or infinity (INV-2 support; <see cref="Synthesizer"/> remains
    /// the final choke point).
    /// </summary>
    public struct BiquadLowPassFilter {

        const float MinCutoffHz = 20f;
        const float MaxCutoffFractionOfSampleRate = 0.45f;
        const float MinResonance = 0.5f;
        const float MaxResonance = 25f;

        readonly bool bypass;
        readonly float b0;
        readonly float b1;
        readonly float b2;
        readonly float a1;
        readonly float a2;

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

            state1 = 0f;
            state2 = 0f;

            if (parameters.CutoffHz >= FilterParameters.Sf2OpenCutoffHz) {
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
            float cutoff = Clamp(parameters.CutoffHz, MinCutoffHz, maxCutoff);
            float q = Clamp(parameters.Resonance, MinResonance, MaxResonance);

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

        static float Clamp(float value, float min, float max) {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}

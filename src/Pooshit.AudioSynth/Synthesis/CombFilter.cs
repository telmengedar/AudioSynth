using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// One Schroeder comb section: a single delay line whose feedback path passes through a one-pole
    /// damping low-pass before being scaled by <see cref="feedback"/> and folded back into the line, so
    /// every pass both echoes and warms/darkens the tail. Eight parallel instances per channel form one of
    /// <see cref="Reverb"/>'s comb banks. Ctor-allocated buffer; <see cref="Process"/> is a single-sample
    /// step, allocation-free. Internal: a pure implementation detail of <see cref="Reverb"/>, with no
    /// external consumer.
    /// </summary>
    internal sealed class CombFilter {

        readonly float[] buffer;
        readonly float feedback;
        readonly float damp1;
        readonly float damp2;
        int index;
        float filterStore;

        /// <summary>
        /// Creates a <see cref="CombFilter"/> with a delay line of <paramref name="length"/> samples,
        /// silent until fed.
        /// </summary>
        /// <param name="length">delay length in samples; must be at least 1</param>
        /// <param name="feedback">feedback gain; must be in [0, 1) for BIBO stability</param>
        /// <param name="damping">in-feedback one-pole damping amount in [0, 1]</param>
        public CombFilter(int length, float feedback, float damping) {
            if (length < 1)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Delay length must be at least 1.");
            if (feedback < 0f || feedback >= 1f)
                throw new ArgumentOutOfRangeException(nameof(feedback), feedback, "Feedback must be in [0, 1) for stability.");
            if (damping < 0f || damping > 1f)
                throw new ArgumentOutOfRangeException(nameof(damping), damping, "Damping must be in [0, 1].");

            buffer = new float[length];
            this.feedback = feedback;
            damp1 = damping;
            damp2 = 1f - damping;
            index = 0;
            filterStore = 0f;
        }

        /// <summary>
        /// Filters one input sample and advances the delay line by one sample.
        /// </summary>
        /// <param name="input">the input sample</param>
        /// <returns>the comb-filtered output sample</returns>
        public float Process(float input) {
            float output = buffer[index];
            filterStore = output * damp2 + filterStore * damp1;
            buffer[index] = input + filterStore * feedback;
            index++;
            if (index >= buffer.Length)
                index = 0;
            return output;
        }
    }
}

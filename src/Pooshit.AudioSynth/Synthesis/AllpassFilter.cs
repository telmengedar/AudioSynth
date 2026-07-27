using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// One Schroeder allpass section: a single delay line with fixed feedback <see cref="FixedFeedback"/>,
    /// used four in series per channel inside <see cref="Reverb"/> to diffuse a comb bank's output into a
    /// smooth, echo-free tail without colouring its magnitude spectrum. Ctor-allocated buffer;
    /// <see cref="Process"/> is a single-sample read/write/advance step, so it is allocation-free and never
    /// itself a source of block-size coupling. Internal: a pure implementation detail of <see cref="Reverb"/>,
    /// with no external consumer.
    /// </summary>
    internal sealed class AllpassFilter {

        /// <summary>Fixed allpass feedback (standard Freeverb tuning); unconditionally stable at any delay length.</summary>
        const float FixedFeedback = 0.5f;

        readonly float[] buffer;
        int index;

        /// <summary>
        /// Creates an <see cref="AllpassFilter"/> with a delay line of <paramref name="length"/> samples,
        /// silent until fed.
        /// </summary>
        /// <param name="length">delay length in samples; must be at least 1</param>
        public AllpassFilter(int length) {
            if (length < 1)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Delay length must be at least 1.");
            buffer = new float[length];
            index = 0;
        }

        /// <summary>
        /// Filters one input sample and advances the delay line by one sample.
        /// </summary>
        /// <param name="input">the input sample</param>
        /// <returns>the allpass-filtered output sample</returns>
        public float Process(float input) {
            float bufferedValue = buffer[index];
            float output = bufferedValue - input;
            buffer[index] = input + bufferedValue * FixedFeedback;
            index++;
            if (index >= buffer.Length)
                index = 0;
            return output;
        }
    }
}

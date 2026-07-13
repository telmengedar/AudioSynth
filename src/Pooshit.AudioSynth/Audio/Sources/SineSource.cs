using System;

namespace Pooshit.AudioSynth.Audio.Sources {

    /// <summary>
    /// Endless sine tone used to prove the pull seam end to end; phase is kept bounded to avoid the long-run precision drift seen in the legacy generators.
    /// </summary>
    public sealed class SineSource : IAudioSource {

        const double TwoPi = 2.0 * Math.PI;

        readonly double increment;
        readonly float amplitude;
        double phase;

        /// <summary>
        /// Creates a sine source at the given frequency and amplitude.
        /// </summary>
        /// <param name="format">output format</param>
        /// <param name="frequencyHz">tone frequency in Hertz</param>
        /// <param name="amplitude">peak amplitude in the range 0..1</param>
        public SineSource(AudioFormat format, double frequencyHz, float amplitude = 0.25f) {
            if (frequencyHz <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequencyHz));
            Format = format;
            this.amplitude = amplitude;
            increment = TwoPi * frequencyHz / format.SampleRate;
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            int channels = Format.Channels;
            int frames = destination.Length / channels;
            int index = 0;

            for (int frame = 0; frame < frames; frame++) {
                float value = amplitude * (float)Math.Sin(phase);
                for (int channel = 0; channel < channels; channel++)
                    destination[index++] = value;

                phase += increment;
                if (phase >= TwoPi)
                    phase -= TwoPi;
            }

            return index;
        }
    }
}

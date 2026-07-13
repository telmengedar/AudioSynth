using System;

namespace Pooshit.AudioSynth.Audio {

    /// <summary>
    /// Immutable description of a PCM stream's sample rate and channel count.
    /// </summary>
    public readonly struct AudioFormat : IEquatable<AudioFormat> {

        /// <summary>
        /// Creates an audio format.
        /// </summary>
        /// <param name="sampleRate">frames per second (e.g. 44100)</param>
        /// <param name="channels">interleaved channel count (1 = mono, 2 = stereo)</param>
        public AudioFormat(int sampleRate, int channels) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels));
            SampleRate = sampleRate;
            Channels = channels;
        }

        /// <summary>
        /// Frames per second.
        /// </summary>
        public int SampleRate { get; }

        /// <summary>
        /// Interleaved channel count.
        /// </summary>
        public int Channels { get; }

        /// <inheritdoc/>
        public bool Equals(AudioFormat other) => SampleRate == other.SampleRate && Channels == other.Channels;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is AudioFormat other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (SampleRate * 397) ^ Channels;

        /// <inheritdoc/>
        public override string ToString() => $"{SampleRate}Hz x{Channels}";
    }
}

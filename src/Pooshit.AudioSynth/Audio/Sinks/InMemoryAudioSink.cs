using System;
using System.Collections.Generic;

namespace Pooshit.AudioSynth.Audio.Sinks {

    /// <summary>
    /// Sink that accumulates all written samples in memory; used for offline capture and as a test double for the pull seam.
    /// </summary>
    public sealed class InMemoryAudioSink : IAudioSink {

        readonly List<float> samples;

        /// <summary>
        /// Creates an in-memory sink for the given format.
        /// </summary>
        public InMemoryAudioSink(AudioFormat format) {
            Format = format;
            samples = new List<float>();
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <summary>
        /// Count of interleaved samples captured so far.
        /// </summary>
        public int SampleCount => samples.Count;

        /// <inheritdoc/>
        public void Write(ReadOnlySpan<float> source) {
            for (int i = 0; i < source.Length; i++)
                samples.Add(source[i]);
        }

        /// <summary>
        /// Returns a copy of the captured samples.
        /// </summary>
        public float[] ToArray() => samples.ToArray();
    }
}

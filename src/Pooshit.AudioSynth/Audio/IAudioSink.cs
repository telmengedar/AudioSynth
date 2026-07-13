using System;

namespace Pooshit.AudioSynth.Audio {

    /// <summary>
    /// Consumer of interleaved float PCM. Adapters (WAV writer, NAudio, in-memory) implement this; the core never depends on a concrete sink.
    /// </summary>
    public interface IAudioSink {

        /// <summary>
        /// Format the sink expects.
        /// </summary>
        AudioFormat Format { get; }

        /// <summary>
        /// Consumes a block of interleaved samples.
        /// </summary>
        void Write(ReadOnlySpan<float> source);
    }
}

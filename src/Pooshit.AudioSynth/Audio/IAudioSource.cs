using System;

namespace Pooshit.AudioSynth.Audio {

    /// <summary>
    /// Pull-based producer of interleaved float PCM. The central seam: a real-time sink or an offline renderer drives it by pulling blocks on demand.
    /// </summary>
    public interface IAudioSource {

        /// <summary>
        /// Format the source emits.
        /// </summary>
        AudioFormat Format { get; }

        /// <summary>
        /// Fills the destination with interleaved samples and returns the count written; a value below the span length signals end of stream.
        /// </summary>
        int Read(Span<float> destination);
    }
}

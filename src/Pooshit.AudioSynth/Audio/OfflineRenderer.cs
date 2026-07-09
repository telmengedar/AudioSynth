using System;

namespace Pooshit.AudioSynth.Audio {

    /// <summary>
    /// Offline driver that pumps an <see cref="IAudioSource"/> into an <see cref="IAudioSink"/> block by block, allocating only the reusable block buffer once.
    /// </summary>
    public static class OfflineRenderer {

        /// <summary>
        /// Number of frames pulled per iteration; steady-state render allocates nothing.
        /// </summary>
        public const int BlockFrames = 512;

        /// <summary>
        /// Renders up to the requested frame count from source into sink and returns the number of frames actually rendered.
        /// </summary>
        /// <param name="source">pull source whose format must match the sink</param>
        /// <param name="sink">destination consumer</param>
        /// <param name="frames">total frames to render</param>
        public static long Render(IAudioSource source, IAudioSink sink, long frames) {
            if (source is null)
                throw new ArgumentNullException(nameof(source));
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));
            if (!source.Format.Equals(sink.Format))
                throw new ArgumentException($"Source format {source.Format} does not match sink format {sink.Format}.");
            if (frames < 0)
                throw new ArgumentOutOfRangeException(nameof(frames));

            int channels = source.Format.Channels;
            float[] buffer = new float[BlockFrames * channels];
            long rendered = 0;

            while (rendered < frames) {
                long remainingFrames = frames - rendered;
                int blockFrames = remainingFrames < BlockFrames ? (int)remainingFrames : BlockFrames;
                int requested = blockFrames * channels;

                Span<float> slice = buffer.AsSpan(0, requested);
                int produced = source.Read(slice);
                if (produced <= 0)
                    break;

                sink.Write(slice.Slice(0, produced));
                rendered += produced / channels;

                if (produced < requested)
                    break;
            }

            return rendered;
        }
    }
}

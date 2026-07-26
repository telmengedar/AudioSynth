using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable descriptor of a playable mono sample region: buffer reference, loop boundaries,
    /// source sample rate, root key, and pitch correction; runtime playback state lives in the voice.
    /// </summary>
    public sealed class SampleRegion {

        /// <summary>
        /// Creates a <see cref="SampleRegion"/>.
        /// </summary>
        /// <param name="buffer">mono float PCM samples shared across all voices that play this region</param>
        /// <param name="start">inclusive start index into <paramref name="buffer"/></param>
        /// <param name="end">exclusive end index into <paramref name="buffer"/></param>
        /// <param name="loopStart">inclusive loop-start index; ignored when <see cref="LoopMode"/> is <see cref="LoopMode.NoLoop"/></param>
        /// <param name="loopEnd">exclusive loop-end index; ignored when <see cref="LoopMode"/> is <see cref="LoopMode.NoLoop"/></param>
        /// <param name="loopMode">loop behaviour for this region</param>
        /// <param name="sourceSampleRate">sample rate of the source recording in frames per second</param>
        /// <param name="rootKey">MIDI key number at which the sample plays at its original pitch (0–127)</param>
        /// <param name="pitchCorrectionCents">fine-tuning offset in cents applied on top of the key transposition</param>
        /// <param name="envelope">volume-envelope parameters shaping this region's amplitude over the note's life</param>
        /// <param name="filter">low-pass filter parameters shaping this region's timbre before the amplifier</param>
        public SampleRegion(
            float[] buffer,
            int start,
            int end,
            int loopStart,
            int loopEnd,
            LoopMode loopMode,
            int sourceSampleRate,
            int rootKey,
            int pitchCorrectionCents,
            EnvelopeParameters envelope,
            FilterParameters filter) {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (start < 0 || start >= buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (end <= start || end > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(end));
            if (loopMode == LoopMode.Continuous) {
                if (loopStart < start || loopStart >= end)
                    throw new ArgumentOutOfRangeException(nameof(loopStart));
                if (loopEnd <= loopStart || loopEnd > end)
                    throw new ArgumentOutOfRangeException(nameof(loopEnd));
            }
            if (sourceSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            if (rootKey < 0 || rootKey > 127)
                throw new ArgumentOutOfRangeException(nameof(rootKey));
            Buffer = buffer;
            Start = start;
            End = end;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
            LoopMode = loopMode;
            SourceSampleRate = sourceSampleRate;
            RootKey = rootKey;
            PitchCorrectionCents = pitchCorrectionCents;
            Envelope = envelope;
            Filter = filter;
        }

        /// <summary>
        /// Mono PCM sample data shared across all voices that play this region.
        /// </summary>
        public float[] Buffer { get; }

        /// <summary>
        /// Inclusive start index into <see cref="Buffer"/>.
        /// </summary>
        public int Start { get; }

        /// <summary>
        /// Exclusive end index into <see cref="Buffer"/>.
        /// </summary>
        public int End { get; }

        /// <summary>
        /// Inclusive loop-start index; relevant only when <see cref="LoopMode"/> is <see cref="LoopMode.Continuous"/>.
        /// </summary>
        public int LoopStart { get; }

        /// <summary>
        /// Exclusive loop-end index; relevant only when <see cref="LoopMode"/> is <see cref="LoopMode.Continuous"/>.
        /// </summary>
        public int LoopEnd { get; }

        /// <summary>
        /// Loop behaviour: no-loop one-shot or continuous looping between loop points.
        /// </summary>
        public LoopMode LoopMode { get; }

        /// <summary>
        /// Sample rate of the source recording in frames per second.
        /// </summary>
        public int SourceSampleRate { get; }

        /// <summary>
        /// MIDI key number at which the sample plays at its original pitch (0–127).
        /// </summary>
        public int RootKey { get; }

        /// <summary>
        /// Fine-tuning offset in cents added to the key transposition.
        /// </summary>
        public int PitchCorrectionCents { get; }

        /// <summary>
        /// Volume-envelope parameters shaping this region's amplitude over the note's life.
        /// </summary>
        public EnvelopeParameters Envelope { get; }

        /// <summary>
        /// Low-pass filter parameters shaping this region's timbre before the amplifier stage.
        /// </summary>
        public FilterParameters Filter { get; }
    }
}

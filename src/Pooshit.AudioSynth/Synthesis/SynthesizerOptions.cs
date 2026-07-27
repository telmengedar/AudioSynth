using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable configuration for a <see cref="Synthesizer"/>; all fields carry sensible defaults.
    /// </summary>
    public sealed class SynthesizerOptions {

        /// <summary>
        /// Default output sample rate in frames per second.
        /// </summary>
        public const int DefaultSampleRate = 44100;

        /// <summary>
        /// Default interleaved channel count.
        /// </summary>
        public const int DefaultChannels = 2;

        /// <summary>
        /// Default number of frames in each internal render block.
        /// </summary>
        public const int DefaultBlockFrames = 64;

        /// <summary>
        /// Default maximum simultaneous voices.
        /// </summary>
        public const int DefaultMaxVoices = 32;

        /// <summary>
        /// Creates a <see cref="SynthesizerOptions"/> with the supplied values, validating each one.
        /// </summary>
        /// <param name="sampleRate">frames per second; must be positive</param>
        /// <param name="channels">interleaved channel count; must be positive</param>
        /// <param name="blockFrames">internal render block size in frames; must be positive</param>
        /// <param name="maxVoices">maximum simultaneous voices; must be positive</param>
        /// <param name="reverb">master-bus reverb settings; <c>null</c> (the default) leaves the master path dry</param>
        public SynthesizerOptions(
            int sampleRate = DefaultSampleRate,
            int channels = DefaultChannels,
            int blockFrames = DefaultBlockFrames,
            int maxVoices = DefaultMaxVoices,
            ReverbSettings? reverb = null) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
            if (blockFrames <= 0)
                throw new ArgumentOutOfRangeException(nameof(blockFrames), blockFrames, "Block frames must be positive.");
            if (maxVoices <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxVoices), maxVoices, "Max voices must be positive.");
            SampleRate = sampleRate;
            Channels = channels;
            BlockFrames = blockFrames;
            MaxVoices = maxVoices;
            Reverb = reverb;
        }

        /// <summary>
        /// Output sample rate in frames per second.
        /// </summary>
        public int SampleRate { get; }

        /// <summary>
        /// Interleaved channel count.
        /// </summary>
        public int Channels { get; }

        /// <summary>
        /// Number of frames in each internal render block.
        /// </summary>
        public int BlockFrames { get; }

        /// <summary>
        /// Maximum number of simultaneous voices; new notes are dropped when the pool is full.
        /// </summary>
        public int MaxVoices { get; }

        /// <summary>
        /// Master-bus reverb settings; <c>null</c> (the default) means no reverb is constructed and the
        /// master path is unchanged. Only takes effect when <see cref="Channels"/> equals 2 (stereo).
        /// </summary>
        public ReverbSettings? Reverb { get; }
    }
}

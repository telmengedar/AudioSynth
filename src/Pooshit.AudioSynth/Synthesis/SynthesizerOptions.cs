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
        /// Default master output gain (unity; matches pre-existing output exactly).
        /// </summary>
        public const float DefaultMasterGain = 1.0f;

        /// <summary>
        /// Creates a <see cref="SynthesizerOptions"/> with the supplied values, validating each one.
        /// </summary>
        /// <param name="sampleRate">frames per second; must be positive</param>
        /// <param name="channels">interleaved channel count; must be positive</param>
        /// <param name="blockFrames">internal render block size in frames; must be positive</param>
        /// <param name="maxVoices">maximum simultaneous voices; must be positive</param>
        /// <param name="reverb">master-bus reverb settings; <c>null</c> (the default) leaves the master path dry</param>
        /// <param name="globalReverb">
        /// reverb routing selector: <c>false</c> (the default) honours per-channel/per-voice reverb send
        /// (CC91 × SF2 gen-16); <c>true</c> reverts to a uniform master insert where every voice sends
        /// fully, reproducing the pre-send-bus (PR 16) render bit-for-bit
        /// </param>
        /// <param name="chorus">master-bus chorus settings; <c>null</c> (the default) leaves the master path unaffected by chorus</param>
        /// <param name="globalChorus">
        /// chorus routing selector: <c>false</c> (the default) honours per-channel/per-voice chorus send
        /// (CC93 + SF2 gen-15, additive); <c>true</c> routes chorus as a uniform master insert where every
        /// voice sends fully, mirroring <paramref name="globalReverb"/>
        /// </param>
        /// <param name="masterGain">
        /// scalar applied to the summed master bus before the soft-clip stage (mirrors FluidSynth's
        /// <c>synth.gain</c>); must be non-negative and non-NaN; defaults to unity, which reproduces the
        /// pre-existing output exactly
        /// </param>
        public SynthesizerOptions(
            int sampleRate = DefaultSampleRate,
            int channels = DefaultChannels,
            int blockFrames = DefaultBlockFrames,
            int maxVoices = DefaultMaxVoices,
            ReverbSettings? reverb = null,
            bool globalReverb = false,
            ChorusSettings? chorus = null,
            bool globalChorus = false,
            float masterGain = DefaultMasterGain) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
            if (blockFrames <= 0)
                throw new ArgumentOutOfRangeException(nameof(blockFrames), blockFrames, "Block frames must be positive.");
            if (maxVoices <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxVoices), maxVoices, "Max voices must be positive.");
            if (float.IsNaN(masterGain) || masterGain < 0f)
                throw new ArgumentOutOfRangeException(nameof(masterGain), masterGain, "Master gain must be non-negative and non-NaN.");
            SampleRate = sampleRate;
            Channels = channels;
            BlockFrames = blockFrames;
            MaxVoices = maxVoices;
            Reverb = reverb;
            GlobalReverb = globalReverb;
            Chorus = chorus;
            GlobalChorus = globalChorus;
            MasterGain = masterGain;
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

        /// <summary>
        /// Reverb routing selector. <c>false</c> (the default): each voice feeds the reverb through a
        /// per-channel-weighted send bus, honouring CC91 (<see cref="ISynthesizer.SetChannelReverbSend"/>)
        /// combined with each voice's SF2 gen-16 send. <c>true</c>: the reverb is a uniform master insert
        /// (every voice sends fully), reproducing the pre-send-bus master-insert render bit-for-bit. Only
        /// meaningful when <see cref="Reverb"/> is configured and <see cref="Channels"/> is 2 (stereo).
        /// </summary>
        public bool GlobalReverb { get; }

        /// <summary>
        /// Master-bus chorus settings; <c>null</c> (the default) means no chorus is constructed and the
        /// master path is unaffected by chorus. Only takes effect when <see cref="Channels"/> equals 2
        /// (stereo).
        /// </summary>
        public ChorusSettings? Chorus { get; }

        /// <summary>
        /// Chorus routing selector. <c>false</c> (the default): each voice feeds the chorus through a
        /// per-channel-weighted send bus, honouring CC93 (<see cref="ISynthesizer.SetChannelChorusSend"/>)
        /// combined additively with each voice's SF2 gen-15 send. <c>true</c>: the chorus is a uniform
        /// master insert (every voice sends fully), mirroring <see cref="GlobalReverb"/>. Only meaningful
        /// when <see cref="Chorus"/> is configured and <see cref="Channels"/> is 2 (stereo).
        /// </summary>
        public bool GlobalChorus { get; }

        /// <summary>
        /// Master output gain applied to the summed master bus before the soft-clip stage; unity (1.0)
        /// reproduces the pre-existing output exactly.
        /// </summary>
        public float MasterGain { get; }
    }
}

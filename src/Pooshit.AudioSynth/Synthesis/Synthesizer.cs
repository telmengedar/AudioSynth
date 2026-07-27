using System;
using Pooshit.AudioSynth.Audio;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Pull-based voice engine that implements <see cref="ISynthesizer"/>; turns MIDI-style note events
    /// into voices, renders them in fixed-size internal blocks, mixes with equal-power centre pan, and
    /// passes every output frame through a single NaN/Inf-safe finalize choke point (INV-2).
    /// All pre-allocated buffers are sized at construction; steady-state <see cref="Read"/> allocates nothing.
    /// </summary>
    public sealed class Synthesizer : ISynthesizer {

        const int ChannelCount = 16;

        readonly SynthesizerOptions options;
        readonly IPatch[] channelPatch;
        readonly VoiceSlot[] pool;
        readonly float[] scratch;
        readonly float[] master;
        readonly float panGain;

        /// <summary>
        /// Creates a <see cref="Synthesizer"/> with the given options; <paramref name="defaultPatch"/>
        /// fills all 16 channels until a <see cref="SetChannelPatch"/> call overrides one.
        /// </summary>
        /// <param name="options">immutable engine configuration</param>
        /// <param name="defaultPatch">initial patch for every channel</param>
        public Synthesizer(SynthesizerOptions options, IPatch defaultPatch) {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (defaultPatch is null) throw new ArgumentNullException(nameof(defaultPatch));
            Format = new AudioFormat(options.SampleRate, options.Channels);
            channelPatch = new IPatch[ChannelCount];
            for (int i = 0; i < channelPatch.Length; i++)
                channelPatch[i] = defaultPatch;
            pool = new VoiceSlot[options.MaxVoices];
            scratch = new float[options.BlockFrames];
            master = new float[options.BlockFrames * options.Channels];
            panGain = (float)(1.0 / Math.Sqrt(options.Channels));
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <inheritdoc/>
        public void SetChannelPatch(int channel, IPatch patch) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");
            if (patch is null) throw new ArgumentNullException(nameof(patch));
            channelPatch[channel] = patch;
        }

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be in [0,{ChannelCount - 1}].");

            int freeSlot = FindFreeSlot();
            if (freeSlot < 0)
                return;

            IVoice voice = channelPatch[channel].StartVoice(key, velocity);
            ref VoiceSlot slot = ref pool[freeSlot];
            slot.IsOccupied = true;
            slot.Channel = channel;
            slot.Key = key;
            slot.Voice = voice;
        }

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) {
            for (int i = 0; i < pool.Length; i++) {
                ref VoiceSlot slot = ref pool[i];
                if (slot.IsOccupied && slot.Channel == channel && slot.Key == key)
                    slot.Voice!.Release();
            }
        }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            if (destination.Length % options.Channels != 0)
                throw new ArgumentException(
                    $"destination length ({destination.Length}) must be a multiple of the channel count ({options.Channels}).",
                    nameof(destination));

            int channels = options.Channels;
            int blockFrames = options.BlockFrames;
            int totalSamples = destination.Length;
            int written = 0;

            while (written < totalSamples) {
                int remainingSamples = totalSamples - written;
                int blockSamples = remainingSamples < blockFrames * channels
                    ? remainingSamples
                    : blockFrames * channels;
                int frames = blockSamples / channels;

                Span<float> masterSlice = master.AsSpan(0, frames * channels);
                masterSlice.Clear();

                Span<float> scratchSlice = scratch.AsSpan(0, frames);

                for (int v = 0; v < pool.Length; v++) {
                    ref VoiceSlot slot = ref pool[v];
                    if (!slot.IsOccupied)
                        continue;

                    scratchSlice.Clear();
                    slot.Voice!.RenderBlock(scratchSlice);

                    for (int frame = 0; frame < frames; frame++) {
                        float mixed = scratchSlice[frame] * panGain;
                        int baseIndex = frame * channels;
                        for (int ch = 0; ch < channels; ch++)
                            masterSlice[baseIndex + ch] += mixed;
                    }

                    if (!slot.Voice.IsActive) {
                        slot.IsOccupied = false;
                        slot.Voice = null;
                    }
                }

                Finalize(masterSlice);

                masterSlice.CopyTo(destination.Slice(written));
                written += masterSlice.Length;
            }

            return totalSamples;
        }

        int FindFreeSlot() {
            for (int i = 0; i < pool.Length; i++) {
                if (!pool[i].IsOccupied)
                    return i;
            }
            return -1;
        }

        static void Finalize(Span<float> block) {
            for (int i = 0; i < block.Length; i++) {
                float x = block[i];
                if (float.IsNaN(x) || float.IsInfinity(x)) {
                    block[i] = 0f;
                } else if (x > 1f) {
                    block[i] = 1f;
                } else if (x < -1f) {
                    block[i] = -1f;
                }
            }
        }
    }
}

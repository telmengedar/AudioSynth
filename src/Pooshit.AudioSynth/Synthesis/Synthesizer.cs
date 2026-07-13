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

        readonly SynthesizerOptions _options;
        readonly IPatch _defaultPatch;
        readonly VoiceSlot[] _pool;
        readonly float[] _scratch;
        readonly float[] _master;
        readonly float _panGain;

        /// <summary>
        /// Creates a <see cref="Synthesizer"/> with the given options and a single default patch
        /// used for every note on every channel.
        /// </summary>
        /// <param name="options">immutable engine configuration</param>
        /// <param name="defaultPatch">patch used to start voices for all note-on events</param>
        public Synthesizer(SynthesizerOptions options, IPatch defaultPatch) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _defaultPatch = defaultPatch ?? throw new ArgumentNullException(nameof(defaultPatch));
            Format = new AudioFormat(options.SampleRate, options.Channels);
            _pool = new VoiceSlot[options.MaxVoices];
            _scratch = new float[options.BlockFrames];
            _master = new float[options.BlockFrames * options.Channels];
            _panGain = (float)(1.0 / Math.Sqrt(options.Channels));
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) {
            int freeSlot = FindFreeSlot();
            if (freeSlot < 0)
                return;

            IVoice voice = _defaultPatch.StartVoice(key, velocity);
            ref VoiceSlot slot = ref _pool[freeSlot];
            slot.IsOccupied = true;
            slot.Channel = channel;
            slot.Key = key;
            slot.Voice = voice;
        }

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) {
            for (int i = 0; i < _pool.Length; i++) {
                ref VoiceSlot slot = ref _pool[i];
                if (slot.IsOccupied && slot.Channel == channel && slot.Key == key)
                    slot.Voice!.Release();
            }
        }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            int channels = _options.Channels;
            int blockFrames = _options.BlockFrames;
            int totalSamples = destination.Length;
            int written = 0;

            while (written < totalSamples) {
                int remainingSamples = totalSamples - written;
                int blockSamples = remainingSamples < blockFrames * channels
                    ? remainingSamples
                    : blockFrames * channels;
                int frames = blockSamples / channels;

                Span<float> masterSlice = _master.AsSpan(0, frames * channels);
                masterSlice.Clear();

                Span<float> scratchSlice = _scratch.AsSpan(0, frames);

                for (int v = 0; v < _pool.Length; v++) {
                    ref VoiceSlot slot = ref _pool[v];
                    if (!slot.IsOccupied)
                        continue;

                    scratchSlice.Clear();
                    slot.Voice!.RenderBlock(scratchSlice);

                    for (int frame = 0; frame < frames; frame++) {
                        float mixed = scratchSlice[frame] * _panGain;
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
            for (int i = 0; i < _pool.Length; i++) {
                if (!_pool[i].IsOccupied)
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

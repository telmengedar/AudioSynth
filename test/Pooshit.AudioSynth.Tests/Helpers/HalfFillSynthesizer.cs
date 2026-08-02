using System;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="ISynthesizer"/> whose <see cref="Read"/> deliberately underfills (returns half the
    /// requested frames), so a driver's starved-source stall guard can be exercised. All control calls no-op.
    /// </summary>
    internal sealed class HalfFillSynthesizer : ISynthesizer {

        internal HalfFillSynthesizer(AudioFormat format) {
            Format = format;
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            int channels = Format.Channels;
            int frames = destination.Length / channels;
            int filled = frames / 2;
            destination.Slice(0, filled * channels).Clear();
            return filled * channels;
        }

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) { }

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) { }

        /// <inheritdoc/>
        public void SetChannelPatch(int channel, IPatch patch) { }

        /// <inheritdoc/>
        public void SetChannelGain(int channel, float gain) { }

        /// <inheritdoc/>
        public void SetChannelPitchBend(int channel, float semitones) { }

        /// <inheritdoc/>
        public void SetChannelModulation(int channel, float amount) { }

        /// <inheritdoc/>
        public void SetChannelPan(int channel, float pan) { }

        /// <inheritdoc/>
        public void SetChannelReverbSend(int channel, float level) { }

        /// <inheritdoc/>
        public void SetChannelChorusSend(int channel, float level) { }

        /// <inheritdoc/>
        public void SetChannelSustain(int channel, bool held) { }

        /// <inheritdoc/>
        public void SilenceChannel(int channel) { }

        /// <inheritdoc/>
        public void ReleaseAllNotes(int channel) { }

        /// <inheritdoc/>
        public void SetMasterGain(float gain) { }
    }
}

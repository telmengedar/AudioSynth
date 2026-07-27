using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="ISynthesizer"/> that records every <see cref="SetChannelPatch"/>,
    /// <see cref="NoteOn"/> and <see cref="NoteOff"/> call instead of rendering; <see cref="Read"/>
    /// fills silence so it can still drive <see cref="Pooshit.AudioSynth.Sequencing.MidiSequencer.Render"/>.
    /// </summary>
    internal sealed class RecordingSynthesizer : ISynthesizer {

        internal RecordingSynthesizer(AudioFormat format) {
            Format = format;
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <summary>
        /// Every (channel, patch) pair passed to <see cref="SetChannelPatch"/>, in call order.
        /// </summary>
        internal List<(int Channel, IPatch Patch)> ChannelPatchCalls { get; } = new List<(int, IPatch)>();

        /// <summary>
        /// Every (channel, gain) pair passed to <see cref="SetChannelGain"/>, in call order.
        /// </summary>
        internal List<(int Channel, float Gain)> ChannelGainCalls { get; } = new List<(int, float)>();

        /// <summary>
        /// Every (channel, key, velocity) triple passed to <see cref="NoteOn"/>, in call order.
        /// </summary>
        internal List<(int Channel, int Key, int Velocity)> NoteOnCalls { get; } = new List<(int, int, int)>();

        /// <inheritdoc/>
        public void SetChannelPatch(int channel, IPatch patch) => ChannelPatchCalls.Add((channel, patch));

        /// <inheritdoc/>
        public void SetChannelGain(int channel, float gain) => ChannelGainCalls.Add((channel, gain));

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) => NoteOnCalls.Add((channel, key, velocity));

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) { }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            destination.Clear();
            return destination.Length;
        }
    }
}

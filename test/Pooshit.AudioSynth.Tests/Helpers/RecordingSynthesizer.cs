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
        /// Every (channel, semitones) pair passed to <see cref="SetChannelPitchBend"/>, in call order.
        /// </summary>
        internal List<(int Channel, float Semitones)> ChannelPitchBendCalls { get; } = new List<(int, float)>();

        /// <summary>
        /// Every (channel, amount) pair passed to <see cref="SetChannelModulation"/>, in call order.
        /// </summary>
        internal List<(int Channel, float Amount)> ChannelModulationCalls { get; } = new List<(int, float)>();

        /// <summary>
        /// Every (channel, pan) pair passed to <see cref="SetChannelPan"/>, in call order.
        /// </summary>
        internal List<(int Channel, float Pan)> ChannelPanCalls { get; } = new List<(int, float)>();

        /// <summary>
        /// Every (channel, level) pair passed to <see cref="SetChannelReverbSend"/>, in call order.
        /// </summary>
        internal List<(int Channel, float Level)> ChannelReverbSendCalls { get; } = new List<(int, float)>();

        /// <summary>
        /// Every (channel, level) pair passed to <see cref="SetChannelChorusSend"/>, in call order.
        /// </summary>
        internal List<(int Channel, float Level)> ChannelChorusSendCalls { get; } = new List<(int, float)>();

        /// <summary>
        /// Every (channel, held) pair passed to <see cref="SetChannelSustain"/>, in call order.
        /// </summary>
        internal List<(int Channel, bool Held)> ChannelSustainCalls { get; } = new List<(int, bool)>();

        /// <summary>
        /// Every (channel, key, velocity) triple passed to <see cref="NoteOn"/>, in call order.
        /// </summary>
        internal List<(int Channel, int Key, int Velocity)> NoteOnCalls { get; } = new List<(int, int, int)>();

        /// <summary>
        /// Every channel passed to <see cref="SilenceChannel"/>, in call order.
        /// </summary>
        internal List<int> SilenceChannelCalls { get; } = new List<int>();

        /// <summary>
        /// Every channel passed to <see cref="ReleaseAllNotes"/>, in call order.
        /// </summary>
        internal List<int> ReleaseAllNotesCalls { get; } = new List<int>();

        /// <summary>
        /// Every gain passed to <see cref="SetMasterCalibrationGain"/>, in call order.
        /// </summary>
        internal List<float> MasterCalibrationGainCalls { get; } = new List<float>();

        /// <inheritdoc/>
        public void SetChannelPatch(int channel, IPatch patch) => ChannelPatchCalls.Add((channel, patch));

        /// <inheritdoc/>
        public void SetChannelGain(int channel, float gain) => ChannelGainCalls.Add((channel, gain));

        /// <inheritdoc/>
        public void SetChannelPitchBend(int channel, float semitones) => ChannelPitchBendCalls.Add((channel, semitones));

        /// <inheritdoc/>
        public void SetChannelModulation(int channel, float amount) => ChannelModulationCalls.Add((channel, amount));

        /// <inheritdoc/>
        public void SetChannelPan(int channel, float pan) => ChannelPanCalls.Add((channel, pan));

        /// <inheritdoc/>
        public void SetChannelReverbSend(int channel, float level) => ChannelReverbSendCalls.Add((channel, level));

        /// <inheritdoc/>
        public void SetChannelChorusSend(int channel, float level) => ChannelChorusSendCalls.Add((channel, level));

        /// <inheritdoc/>
        public void SetChannelSustain(int channel, bool held) => ChannelSustainCalls.Add((channel, held));

        /// <inheritdoc/>
        public void SilenceChannel(int channel) => SilenceChannelCalls.Add(channel);

        /// <inheritdoc/>
        public void ReleaseAllNotes(int channel) => ReleaseAllNotesCalls.Add(channel);

        /// <inheritdoc/>
        public void SetMasterCalibrationGain(float gain) => MasterCalibrationGainCalls.Add(gain);

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

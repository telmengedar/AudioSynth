using System;
using System.Collections.Generic;
using System.Globalization;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="ISynthesizer"/> that logs every call — including <see cref="Read"/>'s
    /// requested frame count — into one ordered <see cref="CallLog"/>, so a test can assert exactly how
    /// many frames were pulled between two dispatched events (sub-block offset proof).
    /// </summary>
    internal sealed class CallLoggingSynthesizer : ISynthesizer {

        internal CallLoggingSynthesizer(AudioFormat format) {
            Format = format;
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <summary>Every call in invocation order, formatted for assertion.</summary>
        internal List<string> CallLog { get; } = new List<string>();

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            destination.Clear();
            int frames = destination.Length / Format.Channels;
            CallLog.Add(Invariant($"Read({frames})"));
            return destination.Length;
        }

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) => CallLog.Add(Invariant($"NoteOn({channel},{key},{velocity})"));

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) => CallLog.Add(Invariant($"NoteOff({channel},{key})"));

        /// <inheritdoc/>
        public void SetChannelPatch(int channel, IPatch patch) => CallLog.Add(Invariant($"SetChannelPatch({channel})"));

        /// <inheritdoc/>
        public void SetChannelGain(int channel, float gain) => CallLog.Add(Invariant($"SetChannelGain({channel},{gain})"));

        /// <inheritdoc/>
        public void SetChannelPitchBend(int channel, float semitones) => CallLog.Add(Invariant($"SetChannelPitchBend({channel},{semitones})"));

        /// <inheritdoc/>
        public void SetChannelModulation(int channel, float amount) => CallLog.Add(Invariant($"SetChannelModulation({channel},{amount})"));

        /// <inheritdoc/>
        public void SetChannelPan(int channel, float pan) => CallLog.Add(Invariant($"SetChannelPan({channel},{pan})"));

        /// <inheritdoc/>
        public void SetChannelReverbSend(int channel, float level) => CallLog.Add(Invariant($"SetChannelReverbSend({channel},{level})"));

        /// <inheritdoc/>
        public void SetChannelChorusSend(int channel, float level) => CallLog.Add(Invariant($"SetChannelChorusSend({channel},{level})"));

        /// <inheritdoc/>
        public void SetChannelSustain(int channel, bool held) => CallLog.Add(Invariant($"SetChannelSustain({channel},{held})"));

        /// <inheritdoc/>
        public void SilenceChannel(int channel) => CallLog.Add(Invariant($"SilenceChannel({channel})"));

        /// <inheritdoc/>
        public void ReleaseAllNotes(int channel) => CallLog.Add(Invariant($"ReleaseAllNotes({channel})"));

        /// <inheritdoc/>
        public void SetMasterGain(float gain) => CallLog.Add(Invariant($"SetMasterGain({gain})"));

        static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }
}

using System;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Drives an <see cref="ISynthesizer"/> from a <see cref="TimedMessageSequence"/>, owning all GM/MIDI
    /// routing semantics: per-channel <c>ProgramChange</c> and channel 9 = percussion (DiVoid #7117).
    /// </summary>
    public static class MidiSequencer {

        /// <summary>
        /// Fixed release tail rendered after the last scheduled event so envelope releases finish
        /// audibly; not a configurable knob (DiVoid #7098 §8).
        /// </summary>
        public const float ReleaseTailSeconds = 3.0f;

        const int ChannelCount = 16;
        const int PercussionChannel = 9;
        const int MelodicBank = 0;
        const int PercussionBank = 128;
        const int DefaultProgram = 0;

        /// <summary>GM-reset default for CC7 (Channel Volume).</summary>
        const int DefaultChannelVolume = 100;

        /// <summary>GM-reset default for CC11 (Expression).</summary>
        const int DefaultExpression = 127;

        /// <summary>Full-scale value for a 7-bit MIDI controller.</summary>
        const int ControllerFullScale = 127;

        /// <summary>
        /// Builds the ordered sample-offset schedule for <paramref name="sequence"/>; pure, no audio
        /// touched. Folds a <c>NoteOn</c> with velocity 0 into its <c>NoteOff</c> equivalent.
        /// </summary>
        /// <param name="sequence">the timed message sequence to schedule</param>
        /// <param name="sampleRate">the target render sample rate, in frames per second</param>
        /// <returns>events ordered non-decreasing by <see cref="ScheduledMidiEvent.SampleOffset"/></returns>
        public static ScheduledMidiEvent[] BuildSchedule(TimedMessageSequence sequence, int sampleRate) {
            if (sequence is null)
                throw new ArgumentNullException(nameof(sequence));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");

            TimedMidiMessage[] timed = sequence.Messages;
            ScheduledMidiEvent[] schedule = new ScheduledMidiEvent[timed.Length];
            for (int i = 0; i < timed.Length; i++) {
                long sampleOffset = (long)Math.Round(timed[i].Time * sampleRate, MidpointRounding.AwayFromZero);
                schedule[i] = new ScheduledMidiEvent(sampleOffset, FoldNoteOnVelocityZero(timed[i].Message));
            }
            return schedule;
        }

        /// <summary>
        /// Renders <paramref name="sequence"/> through <paramref name="synthesizer"/> into
        /// <paramref name="sink"/>, GM-resetting all 16 channels from <paramref name="soundBank"/>
        /// first, then renders the fixed release tail.
        /// </summary>
        /// <param name="sequence">the timed message sequence to render</param>
        /// <param name="synthesizer">the engine driving per-channel patches</param>
        /// <param name="sink">the destination for rendered audio</param>
        /// <param name="soundBank">the (bank, program) patch lookup used for GM routing</param>
        /// <returns>the total number of frames written, including the release tail</returns>
        public static long Render(TimedMessageSequence sequence, ISynthesizer synthesizer, IAudioSink sink, SoundBank soundBank) {
            if (synthesizer is null)
                throw new ArgumentNullException(nameof(synthesizer));
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));
            if (soundBank is null)
                throw new ArgumentNullException(nameof(soundBank));

            int[] cc7 = new int[ChannelCount];
            int[] cc11 = new int[ChannelCount];
            for (int channel = 0; channel < ChannelCount; channel++) {
                synthesizer.SetChannelPatch(channel, ResolveProgramPatch(soundBank, channel, DefaultProgram));
                cc7[channel] = DefaultChannelVolume;
                cc11[channel] = DefaultExpression;
                synthesizer.SetChannelGain(channel, ChannelGain(cc7[channel], cc11[channel]));
            }

            ScheduledMidiEvent[] schedule = BuildSchedule(sequence, synthesizer.Format.SampleRate);
            long cursor = 0;

            foreach (ScheduledMidiEvent scheduled in schedule) {
                long gap = scheduled.SampleOffset - cursor;
                if (gap > 0)
                    cursor += OfflineRenderer.Render(synthesizer, sink, gap);
                ApplyMessage(scheduled.Message, synthesizer, soundBank, cc7, cc11);
            }

            long tailFrames = (long)Math.Round(ReleaseTailSeconds * synthesizer.Format.SampleRate, MidpointRounding.AwayFromZero);
            cursor += OfflineRenderer.Render(synthesizer, sink, tailFrames);
            return cursor;
        }

        static IMidiMessage FoldNoteOnVelocityZero(IMidiMessage message) {
            if (message is ChannelMessage channel && channel.Command == ChannelCommandType.NoteOn && channel.Data2 == 0)
                return new ChannelMessage(ChannelCommandType.NoteOff, channel.MidiChannel, channel.Data1, 0);
            return message;
        }

        static void ApplyMessage(IMidiMessage message, ISynthesizer synthesizer, SoundBank soundBank, int[] cc7, int[] cc11) {
            if (!(message is ChannelMessage channel))
                return;

            switch (channel.Command) {
                case ChannelCommandType.NoteOn:
                    synthesizer.NoteOn(channel.MidiChannel, channel.Data1, channel.Data2);
                    break;
                case ChannelCommandType.NoteOff:
                    synthesizer.NoteOff(channel.MidiChannel, channel.Data1);
                    break;
                case ChannelCommandType.ProgramChange:
                    synthesizer.SetChannelPatch(channel.MidiChannel, ResolveProgramPatch(soundBank, channel.MidiChannel, channel.Data1));
                    break;
                case ChannelCommandType.Controller:
                    if (channel.Data1 == (byte)ControllerType.Volume)
                        cc7[channel.MidiChannel] = channel.Data2;
                    else if (channel.Data1 == (byte)ControllerType.Expression)
                        cc11[channel.MidiChannel] = channel.Data2;
                    else
                        break;
                    synthesizer.SetChannelGain(channel.MidiChannel, ChannelGain(cc7[channel.MidiChannel], cc11[channel.MidiChannel]));
                    break;
            }
        }

        static IPatch ResolveProgramPatch(SoundBank soundBank, int channel, int program) {
            int bank = channel == PercussionChannel ? PercussionBank : MelodicBank;
            return soundBank.GetPatch(bank, program);
        }

        /// <summary>
        /// Combined linear mix gain for CC7 (Volume) and CC11 (Expression):
        /// <c>(cc7/127)² · (cc11/127)²</c> (DiVoid #7126 §9).
        /// </summary>
        /// <param name="cc7">raw CC7 (Channel Volume) value, 0-127</param>
        /// <param name="cc11">raw CC11 (Expression) value, 0-127</param>
        /// <returns>linear gain in [0,1]; 1.0 when both controllers are at full scale</returns>
        static float ChannelGain(int cc7, int cc11) {
            float volume = cc7 / (float)ControllerFullScale;
            float expression = cc11 / (float)ControllerFullScale;
            return volume * volume * expression * expression;
        }
    }
}

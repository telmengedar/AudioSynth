using System;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Drives an <see cref="ISynthesizer"/> from a <see cref="TimedMessageSequence"/> onto a single
    /// default patch; non-note messages are accepted but ignored (multi-timbral is DiVoid #7098 PR 11).
    /// </summary>
    public static class MidiSequencer {

        /// <summary>
        /// Fixed release tail rendered after the last scheduled event so envelope releases finish
        /// audibly; not a configurable knob (DiVoid #7098 §8).
        /// </summary>
        public const float ReleaseTailSeconds = 3.0f;

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
        /// <paramref name="sink"/>, then renders the fixed release tail.
        /// </summary>
        /// <param name="sequence">the timed message sequence to render</param>
        /// <param name="synthesizer">the engine driving the current single default patch</param>
        /// <param name="sink">the destination for rendered audio</param>
        /// <returns>the total number of frames written, including the release tail</returns>
        public static long Render(TimedMessageSequence sequence, ISynthesizer synthesizer, IAudioSink sink) {
            if (synthesizer is null)
                throw new ArgumentNullException(nameof(synthesizer));
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));

            ScheduledMidiEvent[] schedule = BuildSchedule(sequence, synthesizer.Format.SampleRate);
            long cursor = 0;

            foreach (ScheduledMidiEvent scheduled in schedule) {
                long gap = scheduled.SampleOffset - cursor;
                if (gap > 0)
                    cursor += OfflineRenderer.Render(synthesizer, sink, gap);
                ApplyNoteMessage(scheduled.Message, synthesizer);
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

        static void ApplyNoteMessage(IMidiMessage message, ISynthesizer synthesizer) {
            if (!(message is ChannelMessage channel))
                return;

            switch (channel.Command) {
                case ChannelCommandType.NoteOn:
                    synthesizer.NoteOn(channel.MidiChannel, channel.Data1, channel.Data2);
                    break;
                case ChannelCommandType.NoteOff:
                    synthesizer.NoteOff(channel.MidiChannel, channel.Data1);
                    break;
            }
        }
    }
}

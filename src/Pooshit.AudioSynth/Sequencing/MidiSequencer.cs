using System;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Offline entry point: imports MIDI into a <see cref="Sequencing.Timeline.Timeline"/> and drives it
    /// dry through a <see cref="RealtimeSequencer"/> into an <see cref="IAudioSink"/> — the same dispatch
    /// path real-time playback uses, no second GM decode. GM/MIDI routing itself now lives in
    /// <see cref="MidiTimelineImporter"/>.
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

            Timeline.Timeline timeline = MidiTimelineImporter.Import(sequence, synthesizer.Format.SampleRate);
            long releaseTailFrames = (long)Math.Round(ReleaseTailSeconds * synthesizer.Format.SampleRate, MidpointRounding.AwayFromZero);
            RealtimeSequencer driver = new RealtimeSequencer(timeline.Compile(), synthesizer, soundBank, releaseTailFrames);
            return OfflineRenderer.Render(driver, sink, long.MaxValue);
        }

        static IMidiMessage FoldNoteOnVelocityZero(IMidiMessage message) {
            if (message is ChannelMessage channel && channel.Command == ChannelCommandType.NoteOn && channel.Data2 == 0)
                return new ChannelMessage(ChannelCommandType.NoteOff, channel.MidiChannel, channel.Data1, 0);
            return message;
        }
    }
}

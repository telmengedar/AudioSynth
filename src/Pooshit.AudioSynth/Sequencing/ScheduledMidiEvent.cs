using Pooshit.AudioSynth.Formats.Midi;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// One MIDI message placed at an absolute sample offset, the output of
    /// <see cref="MidiSequencer.BuildSchedule"/>.
    /// </summary>
    public sealed class ScheduledMidiEvent {

        /// <summary>
        /// Creates a <see cref="ScheduledMidiEvent"/>.
        /// </summary>
        /// <param name="sampleOffset">the absolute sample offset, from the start of the render</param>
        /// <param name="message">the message to apply at that offset</param>
        public ScheduledMidiEvent(long sampleOffset, IMidiMessage message) {
            SampleOffset = sampleOffset;
            Message = message;
        }

        /// <summary>
        /// The absolute sample offset, from the start of the render.
        /// </summary>
        public long SampleOffset { get; }

        /// <summary>
        /// The message to apply at <see cref="SampleOffset"/>.
        /// </summary>
        public IMidiMessage Message { get; }
    }
}

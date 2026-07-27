using System;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Pairs one <see cref="IMidiMessage"/> with its wall-clock time, in seconds from the start of
    /// the sequence.
    /// </summary>
    public sealed class TimedMidiMessage {

        /// <summary>
        /// Creates a <see cref="TimedMidiMessage"/>.
        /// </summary>
        /// <param name="message">the decoded message</param>
        /// <param name="time">the message's time, in seconds from the start of the sequence</param>
        public TimedMidiMessage(IMidiMessage message, float time) {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Time = time;
        }

        /// <summary>
        /// The decoded message.
        /// </summary>
        public IMidiMessage Message { get; }

        /// <summary>
        /// The message's time, in seconds from the start of the sequence.
        /// </summary>
        public float Time { get; }

        /// <inheritdoc/>
        public override string ToString() {
            return $"{Time:F2}: {Message}";
        }
    }
}

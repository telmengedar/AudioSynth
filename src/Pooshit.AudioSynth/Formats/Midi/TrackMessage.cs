using System;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Pairs one <see cref="IMidiMessage"/> with its absolute tick position within a track.
    /// </summary>
    public sealed class TrackMessage {

        /// <summary>
        /// Creates a <see cref="TrackMessage"/>.
        /// </summary>
        /// <param name="message">the decoded message</param>
        /// <param name="position">the absolute tick position, from the start of the track</param>
        public TrackMessage(IMidiMessage message, int position) {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Position = position;
        }

        /// <summary>
        /// The decoded message.
        /// </summary>
        public IMidiMessage Message { get; }

        /// <summary>
        /// The absolute tick position, from the start of the track.
        /// </summary>
        public int Position { get; }

        /// <inheritdoc/>
        public override string ToString() {
            return $"{Position}: {Message}";
        }
    }
}

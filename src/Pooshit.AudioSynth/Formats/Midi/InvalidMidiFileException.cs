using System;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Thrown when a MIDI stream is malformed, truncated, or violates the untrusted-input boundary,
    /// so callers can distinguish a bad file from a programming error or I/O failure.
    /// </summary>
    public class InvalidMidiFileException : Exception {

        /// <summary>
        /// Creates an <see cref="InvalidMidiFileException"/> with a descriptive message.
        /// </summary>
        public InvalidMidiFileException(string message) : base(message) {
        }

        /// <summary>
        /// Creates an <see cref="InvalidMidiFileException"/> with a descriptive message and an inner exception.
        /// </summary>
        public InvalidMidiFileException(string message, Exception inner) : base(message, inner) {
        }
    }
}

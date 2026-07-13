using System;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// Thrown when an SF2 SoundFont stream is malformed, missing a required chunk, or violates the
    /// untrusted-input validation boundary.  All parser-reachable error paths produce this type so
    /// callers can distinguish a bad file from a programming error or I/O failure.
    /// </summary>
    public class InvalidSoundFontException : Exception {

        /// <summary>
        /// Creates an <see cref="InvalidSoundFontException"/> with a descriptive message.
        /// </summary>
        public InvalidSoundFontException(string message) : base(message) { }

        /// <summary>
        /// Creates an <see cref="InvalidSoundFontException"/> with a descriptive message and an
        /// inner exception.
        /// </summary>
        public InvalidSoundFontException(string message, Exception inner) : base(message, inner) { }
    }
}

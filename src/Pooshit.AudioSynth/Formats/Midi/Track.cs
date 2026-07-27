namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// One parsed <c>MTrk</c> chunk: the raw, tick-ordered messages it contains.
    /// </summary>
    public sealed class Track {

        /// <summary>
        /// Creates a <see cref="Track"/> from its parsed messages.
        /// </summary>
        /// <param name="messages">the track's messages, in ascending tick order</param>
        public Track(TrackMessage[] messages) {
            Messages = messages;
        }

        /// <summary>
        /// The track's messages, in ascending tick order.
        /// </summary>
        public TrackMessage[] Messages { get; }
    }
}

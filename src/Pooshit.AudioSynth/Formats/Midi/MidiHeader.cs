namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// The parsed <c>MThd</c> chunk of a MIDI file: format, track count, and tick-duration division.
    /// </summary>
    public sealed class MidiHeader {

        /// <summary>
        /// Creates a <see cref="MidiHeader"/> from its parsed fields.
        /// </summary>
        /// <param name="format">the SMF format (0, 1, or 2)</param>
        /// <param name="trackCount">the number of <c>MTrk</c> chunks that follow</param>
        /// <param name="division">the raw division field</param>
        /// <param name="sequenceType">how <paramref name="division"/> is interpreted</param>
        public MidiHeader(short format, short trackCount, short division, SequenceType sequenceType) {
            Format = format;
            TrackCount = trackCount;
            Division = division;
            SequenceType = sequenceType;
        }

        /// <summary>
        /// The SMF format (0, 1, or 2).
        /// </summary>
        public short Format { get; }

        /// <summary>
        /// The number of <c>MTrk</c> chunks that follow the header.
        /// </summary>
        public short TrackCount { get; }

        /// <summary>
        /// The raw division field; its meaning depends on <see cref="SequenceType"/>.
        /// </summary>
        public short Division { get; }

        /// <summary>
        /// How <see cref="Division"/> is interpreted.
        /// </summary>
        public SequenceType SequenceType { get; }
    }
}

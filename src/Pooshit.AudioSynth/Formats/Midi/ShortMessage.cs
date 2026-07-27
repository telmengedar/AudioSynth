namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Base for every MIDI message that carries a status byte and up to two data bytes
    /// (channel, system common, and system realtime messages).
    /// </summary>
    public abstract class ShortMessage : IMidiMessage {

        /// <summary>
        /// Creates a <see cref="ShortMessage"/> from its raw status and data bytes.
        /// </summary>
        /// <param name="status">the status byte</param>
        /// <param name="data1">the first data byte</param>
        /// <param name="data2">the second data byte</param>
        protected ShortMessage(byte status, byte data1, byte data2) {
            Status = status;
            Data1 = data1;
            Data2 = data2;
        }

        /// <inheritdoc/>
        public byte Status { get; }

        /// <summary>
        /// The first data byte.
        /// </summary>
        public byte Data1 { get; }

        /// <summary>
        /// The second data byte.
        /// </summary>
        public byte Data2 { get; }

        /// <inheritdoc/>
        public abstract MessageType MessageType { get; }

        /// <inheritdoc/>
        public override string ToString() {
            return $"{Status}, {Data1}, {Data2}";
        }
    }
}

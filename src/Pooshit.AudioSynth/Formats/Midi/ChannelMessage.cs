namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// A channel voice message: a command applied to one of the 16 MIDI channels, carrying
    /// up to two data bytes (e.g. key + velocity for note on/off).
    /// </summary>
    public sealed class ChannelMessage : ShortMessage {

        /// <summary>
        /// Creates a <see cref="ChannelMessage"/> for a single-data-byte command
        /// (<see cref="ChannelCommandType.ProgramChange"/> or <see cref="ChannelCommandType.ChannelPressure"/>).
        /// </summary>
        /// <param name="command">the channel command</param>
        /// <param name="channel">the MIDI channel, 0-15</param>
        /// <param name="data1">the single data byte</param>
        public ChannelMessage(ChannelCommandType command, byte channel, byte data1)
            : base((byte)(channel | (byte)command), data1, 0) {
        }

        /// <summary>
        /// Creates a <see cref="ChannelMessage"/> for a two-data-byte command.
        /// </summary>
        /// <param name="command">the channel command</param>
        /// <param name="channel">the MIDI channel, 0-15</param>
        /// <param name="data1">the first data byte</param>
        /// <param name="data2">the second data byte</param>
        public ChannelMessage(ChannelCommandType command, byte channel, byte data1, byte data2)
            : base((byte)(channel | (byte)command), data1, data2) {
        }

        /// <summary>
        /// The channel command carried in the status byte's high nibble.
        /// </summary>
        public ChannelCommandType Command => (ChannelCommandType)(Status & 0xF0);

        /// <summary>
        /// The MIDI channel carried in the status byte's low nibble.
        /// </summary>
        public byte MidiChannel => (byte)(Status & 0x0F);

        /// <inheritdoc/>
        public override MessageType MessageType => MessageType.Channel;

        /// <summary>
        /// Returns the number of data bytes a channel command uses on the wire (1 for
        /// program change / channel pressure, 2 for every other command).
        /// </summary>
        /// <param name="command">the channel command to test</param>
        /// <returns>1 or 2</returns>
        internal static int DataBytesPerType(ChannelCommandType command) {
            return command == ChannelCommandType.ChannelPressure || command == ChannelCommandType.ProgramChange
                ? 1
                : 2;
        }

        /// <inheritdoc/>
        public override string ToString() {
            return $"{Command}, Channel {MidiChannel}, Key {Data1}, Volume {Data2}";
        }
    }
}

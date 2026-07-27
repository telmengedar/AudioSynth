namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// A system common message (0xF1-0xF6). Not interpreted in increment 1.
    /// </summary>
    public sealed class SysCommonMessage : ShortMessage {

        /// <summary>
        /// Creates a <see cref="SysCommonMessage"/> with no data bytes.
        /// </summary>
        /// <param name="type">the system common sub-type</param>
        public SysCommonMessage(SysCommonMessageType type) : base((byte)type, 0, 0) {
        }

        /// <summary>
        /// Creates a <see cref="SysCommonMessage"/> with one data byte.
        /// </summary>
        /// <param name="type">the system common sub-type</param>
        /// <param name="data1">the first data byte</param>
        public SysCommonMessage(SysCommonMessageType type, byte data1) : base((byte)type, data1, 0) {
        }

        /// <summary>
        /// Creates a <see cref="SysCommonMessage"/> with two data bytes.
        /// </summary>
        /// <param name="type">the system common sub-type</param>
        /// <param name="data1">the first data byte</param>
        /// <param name="data2">the second data byte</param>
        public SysCommonMessage(SysCommonMessageType type, byte data1, byte data2) : base((byte)type, data1, data2) {
        }

        /// <summary>
        /// The system common sub-type carried in the status byte.
        /// </summary>
        public SysCommonMessageType SysCommonType => (SysCommonMessageType)Status;

        /// <inheritdoc/>
        public override MessageType MessageType => MessageType.SystemCommon;
    }
}

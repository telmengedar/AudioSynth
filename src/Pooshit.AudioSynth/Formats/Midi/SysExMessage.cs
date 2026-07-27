using System;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// A system exclusive message; <c>data[0]</c> carries the <see cref="Formats.Midi.SysExType"/>
    /// (0xF0 start or 0xF7 continuation). Not interpreted in increment 1.
    /// </summary>
    public sealed class SysExMessage : IMidiMessage {

        readonly byte[] data;

        /// <summary>
        /// Creates a <see cref="SysExMessage"/> from its raw bytes, the first of which must be
        /// 0xF0 or 0xF7.
        /// </summary>
        /// <param name="data">the raw system exclusive bytes, status byte included</param>
        public SysExMessage(byte[] data) {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// The raw bytes, status byte included.
        /// </summary>
        public byte[] Bytes => data;

        /// <summary>
        /// The raw byte at <paramref name="index"/>.
        /// </summary>
        public byte this[int index] => data[index];

        /// <summary>
        /// The number of raw bytes.
        /// </summary>
        public int Length => data.Length;

        /// <summary>
        /// The system exclusive sub-type carried in the first byte.
        /// </summary>
        public SysExType SysExType => (SysExType)Status;

        /// <inheritdoc/>
        public byte Status => data[0];

        /// <inheritdoc/>
        public MessageType MessageType => MessageType.SystemExclusive;

        /// <inheritdoc/>
        public override string ToString() {
            return $"{SysExType}";
        }
    }
}

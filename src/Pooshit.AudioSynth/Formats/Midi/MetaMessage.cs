using System;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// A file-level meta event (tempo, time signature, track name, end of track, ...);
    /// never transmitted over a MIDI cable, only ever read from or written to a file.
    /// </summary>
    public sealed class MetaMessage : IMidiMessage {

        /// <summary>
        /// The canonical end-of-track meta message.
        /// </summary>
        public static readonly MetaMessage EndOfTrackMessage = new MetaMessage(MetaMessageType.EndOfTrack, Array.Empty<byte>());

        readonly byte[] data;

        /// <summary>
        /// Creates a <see cref="MetaMessage"/> with the given type and payload bytes.
        /// </summary>
        /// <param name="type">the meta event type</param>
        /// <param name="data">the payload bytes; not validated against the type's expected length</param>
        public MetaMessage(MetaMessageType type, byte[] data) {
            MetaType = type;
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// The payload bytes.
        /// </summary>
        public byte[] Bytes => data;

        /// <summary>
        /// The payload byte at <paramref name="index"/>.
        /// </summary>
        public byte this[int index] => data[index];

        /// <summary>
        /// The number of payload bytes.
        /// </summary>
        public int Length => data.Length;

        /// <summary>
        /// The meta event type.
        /// </summary>
        public MetaMessageType MetaType { get; }

        /// <inheritdoc/>
        public byte Status => 0xFF;

        /// <inheritdoc/>
        public MessageType MessageType => MessageType.Meta;

        /// <inheritdoc/>
        public override bool Equals(object? obj) {
            return obj is MetaMessage other && MetaType == other.MetaType && DataEquals(data, other.data);
        }

        /// <inheritdoc/>
        public override int GetHashCode() {
            unchecked {
                int hash = (int)MetaType * 397;
                foreach (byte b in data)
                    hash = (hash * 397) ^ b;
                return hash;
            }
        }

        /// <inheritdoc/>
        public override string ToString() {
            return $"{MetaType} ({data.Length} bytes)";
        }

        static bool DataEquals(byte[] first, byte[] second) {
            if (first.Length != second.Length)
                return false;
            for (int i = 0; i < first.Length; i++) {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }
    }
}

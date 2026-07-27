using System;
using System.IO;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Position-tracking, one-byte-lookahead reader used by <see cref="MidiFile"/> to parse a chunk's
    /// body; every length-prefixed read validates the declared length against the remaining stream.
    /// </summary>
    internal sealed class TrackReader {

        readonly BinaryReader reader;
        int position;
        byte peeked;
        bool hasPeeked;

        /// <summary>
        /// Creates a <see cref="TrackReader"/> over an existing, seekable <see cref="BinaryReader"/>.
        /// </summary>
        /// <param name="reader">the underlying reader</param>
        public TrackReader(BinaryReader reader) {
            this.reader = reader;
        }

        /// <summary>
        /// The number of bytes consumed so far via <see cref="ReadByte"/>, <see cref="ReadBytes"/>,
        /// or <see cref="PeekByte"/>.
        /// </summary>
        public int Position => position;

        /// <summary>
        /// Reads the next byte without consuming it; the following <see cref="ReadByte"/> or
        /// <see cref="ReadBytes"/> call returns it again as the first byte.
        /// </summary>
        public byte PeekByte() {
            peeked = ReadByte();
            hasPeeked = true;
            return peeked;
        }

        /// <summary>
        /// Reads and consumes the next byte.
        /// </summary>
        public byte ReadByte() {
            if (hasPeeked) {
                hasPeeked = false;
                return peeked;
            }
            position++;
            return reader.ReadByte();
        }

        /// <summary>
        /// Reads and consumes <paramref name="count"/> bytes. Throws <see cref="InvalidMidiFileException"/>
        /// when fewer bytes remain in the stream, rather than silently returning a short array.
        /// </summary>
        /// <param name="count">the number of bytes to read; must not be negative</param>
        public byte[] ReadBytes(int count) {
            if (count < 0)
                throw new InvalidMidiFileException($"Cannot read a negative byte count ({count}).");
            if (count == 0)
                return Array.Empty<byte>();

            bool prependPeeked = hasPeeked;
            byte firstByte = peeked;
            hasPeeked = false;
            int bytesToReadFromStream = prependPeeked ? count - 1 : count;

            long remainingInStream = reader.BaseStream.Length - reader.BaseStream.Position;
            if (bytesToReadFromStream > remainingInStream)
                throw new InvalidMidiFileException(
                    $"Attempted to read {count} byte(s) but only " +
                    $"{remainingInStream + (prependPeeked ? 1 : 0)} remain in the stream; file is truncated.");

            byte[] fromStream = reader.ReadBytes(bytesToReadFromStream);
            position += count;

            if (!prependPeeked)
                return fromStream;

            byte[] result = new byte[count];
            result[0] = firstByte;
            fromStream.CopyTo(result, 1);
            return result;
        }

        /// <summary>
        /// Reads a big-endian 32-bit integer without advancing <see cref="Position"/>; used only for
        /// the length prefix preceding a chunk's position-tracked body.
        /// </summary>
        public int ReadInt32() {
            int result = reader.ReadByte();
            result = (result << 8) | reader.ReadByte();
            result = (result << 8) | reader.ReadByte();
            result = (result << 8) | reader.ReadByte();
            return result;
        }

        /// <summary>
        /// Reads a big-endian 16-bit integer directly from the underlying reader, without advancing
        /// <see cref="Position"/>; see <see cref="ReadInt32"/>.
        /// </summary>
        public short ReadInt16() {
            int result = reader.ReadByte();
            result = (result << 8) | reader.ReadByte();
            return (short)result;
        }
    }
}

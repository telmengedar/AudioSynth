using System;
using System.Collections.Generic;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Hand-builds minimal, valid Standard MIDI File byte arrays for parser and sequencer tests,
    /// mirroring <c>Sf2TestBuilder</c>'s role for the SF2 loader tests.
    /// </summary>
    internal static class MidiTestBuilder {

        internal static byte[] BuildFile(short division, params byte[][] trackChunks) {
            List<byte> file = new List<byte>();
            file.AddRange(BuildHeaderChunk(1, (short)trackChunks.Length, division));
            foreach (byte[] chunk in trackChunks)
                file.AddRange(chunk);
            return file.ToArray();
        }

        internal static byte[] BuildHeaderChunk(short format, short trackCount, short division) {
            List<byte> chunk = new List<byte>();
            chunk.AddRange(Tag("MThd"));
            chunk.AddRange(BigEndian32(6));
            chunk.AddRange(BigEndian16(format));
            chunk.AddRange(BigEndian16(trackCount));
            chunk.AddRange(BigEndian16(division));
            return chunk.ToArray();
        }

        internal static byte[] BuildTrackChunk(byte[] body) {
            List<byte> chunk = new List<byte>();
            chunk.AddRange(Tag("MTrk"));
            chunk.AddRange(BigEndian32(body.Length));
            chunk.AddRange(body);
            return chunk.ToArray();
        }

        internal static byte[] EncodeVariableLength(int value) {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            List<byte> bytes = new List<byte> { (byte)(value & 0x7F) };
            value >>= 7;
            while (value > 0) {
                bytes.Insert(0, (byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            return bytes.ToArray();
        }

        internal static byte[] Tag(string tag) {
            byte[] result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)tag[i];
            return result;
        }

        internal static byte[] BigEndian32(int value) {
            return new[] {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }

        internal static byte[] BigEndian16(short value) {
            return new[] {
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }
    }
}

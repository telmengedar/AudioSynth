using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// <see cref="Read"/> parses a Standard MIDI File (<c>MThd</c> + <c>MTrk</c> chunks) into a
    /// faithful model behind an untrusted-input boundary, mirroring <c>Sf2SoundBankLoader</c>.
    /// </summary>
    public sealed class MidiFile {

        MidiFile(MidiHeader header, Track[] tracks) {
            Header = header;
            Tracks = tracks;
        }

        /// <summary>
        /// The parsed <c>MThd</c> chunk.
        /// </summary>
        public MidiHeader Header { get; }

        /// <summary>
        /// The parsed <c>MTrk</c> chunks, in file order.
        /// </summary>
        public Track[] Tracks { get; }

        /// <summary>
        /// Reads a Standard MIDI File from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">a readable stream positioned at the start of an <c>MThd</c> chunk</param>
        /// <returns>the parsed file</returns>
        /// <exception cref="InvalidMidiFileException">
        /// the stream is not a valid MIDI file, is truncated, or declares sizes exceeding its content.
        /// </exception>
        public static MidiFile Read(Stream source) {
            if (source is null)
                throw new ArgumentNullException(nameof(source));

            if (source.CanSeek)
                return ParseSeekable(source);

            using (MemoryStream buffered = new MemoryStream()) {
                source.CopyTo(buffered);
                buffered.Position = 0;
                return ParseSeekable(buffered);
            }
        }

        static MidiFile ParseSeekable(Stream source) {
            using (BinaryReader reader = new BinaryReader(source, Encoding.ASCII, leaveOpen: true)) {
                try {
                    MidiHeader header = ReadHeader(reader);
                    Track[] tracks = ReadTracks(header.TrackCount, reader);
                    return new MidiFile(header, tracks);
                } catch (InvalidMidiFileException) {
                    throw;
                } catch (EndOfStreamException ex) {
                    throw new InvalidMidiFileException(
                        "Unexpected end of stream; the file is truncated or declares sizes exceeding its content.", ex);
                }
            }
        }

        static MidiHeader ReadHeader(BinaryReader reader) {
            string tag = ReadTag(reader);
            if (tag != "MThd")
                throw new InvalidMidiFileException($"Expected 'MThd' chunk tag, got '{tag}'. Not a MIDI file.");

            TrackReader headerReader = new TrackReader(reader);
            int headerLength = headerReader.ReadInt32();
            if (headerLength < 6)
                throw new InvalidMidiFileException(
                    $"MThd chunk declares a length of {headerLength}, too short to contain format/track-count/division.");

            short format = headerReader.ReadInt16();
            short trackCount = headerReader.ReadInt16();
            short division = headerReader.ReadInt16();
            if (trackCount < 0)
                throw new InvalidMidiFileException($"MThd declares a negative track count ({trackCount}).");

            SequenceType sequenceType = (division & 0x8000) != 0 ? SequenceType.Smpte : SequenceType.Ppqn;

            if (headerLength > 6)
                headerReader.ReadBytes(headerLength - 6);

            return new MidiHeader(format, trackCount, division, sequenceType);
        }

        static Track[] ReadTracks(int trackCount, BinaryReader reader) {
            List<Track> tracks = new List<Track>(trackCount > 0 ? trackCount : 0);
            while (tracks.Count < trackCount && reader.BaseStream.Position < reader.BaseStream.Length)
                tracks.Add(ReadTrack(reader));

            if (tracks.Count < trackCount)
                throw new InvalidMidiFileException(
                    $"MThd declares {trackCount} track(s) but only {tracks.Count} were found before the stream ended.");

            return tracks.ToArray();
        }

        static Track ReadTrack(BinaryReader reader) {
            string tag = ReadTag(reader);
            if (tag != "MTrk")
                throw new InvalidMidiFileException($"Expected 'MTrk' chunk tag, got '{tag}'.");

            TrackReader trackReader = new TrackReader(reader);
            int trackLength = trackReader.ReadInt32();
            if (trackLength < 0)
                throw new InvalidMidiFileException($"MTrk chunk declares a negative length ({trackLength}).");

            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (trackLength > remaining)
                throw new InvalidMidiFileException(
                    $"MTrk chunk declares {trackLength} byte(s) but only {remaining} remain in the stream; file is truncated.");

            int ticks = 0;
            byte runningStatus = 0;
            List<TrackMessage> messages = new List<TrackMessage>();

            while (trackReader.Position < trackLength) {
                ticks += ReadVariableLengthQuantity(trackReader);
                IMidiMessage? message = ReadMessage(trackReader, ref runningStatus);
                if (message != null)
                    messages.Add(new TrackMessage(message, ticks));
            }

            return new Track(messages.ToArray());
        }

        static IMidiMessage? ReadMessage(TrackReader reader, ref byte runningStatus) {
            byte next = reader.PeekByte();
            byte status = (next & 0x80) == 0x80 ? reader.ReadByte() : runningStatus;
            return ReadMessage(reader, status, ref runningStatus);
        }

        static IMidiMessage? ReadMessage(TrackReader reader, byte status, ref byte runningStatus) {
            if (status == 0xFF)
                return ReadMetaMessage(reader);

            if (status >= 0x80 && status <= 0xEF)
                return ReadChannelMessage(reader, status, ref runningStatus);

            if (status == 0xF0) {
                runningStatus = 0;
                return ReadSysExMessage(reader, SysExType.Start);
            }

            if (status == 0xF7)
                return ReadSysExContinuation(reader, ref runningStatus);

            if (status >= 0xF1 && status <= 0xF6)
                return ReadSysCommonMessage(reader, status, ref runningStatus);

            if (status >= 0xF8)
                return ReadSysRealtimeMessage(status);

            return null;
        }

        static MetaMessage ReadMetaMessage(TrackReader reader) {
            MetaMessageType metaType = (MetaMessageType)reader.ReadByte();
            if (metaType == MetaMessageType.EndOfTrack) {
                reader.ReadByte();
                return MetaMessage.EndOfTrackMessage;
            }
            int length = ReadVariableLengthQuantity(reader);
            return new MetaMessage(metaType, reader.ReadBytes(length));
        }

        static ChannelMessage ReadChannelMessage(TrackReader reader, byte status, ref byte runningStatus) {
            ChannelCommandType command = (ChannelCommandType)(status & 0xF0);
            byte channel = (byte)(status & 0x0F);
            byte data1 = reader.ReadByte();
            byte data2 = 0;
            if (command != ChannelCommandType.ChannelPressure && command != ChannelCommandType.ProgramChange)
                data2 = reader.ReadByte();
            runningStatus = status;
            return new ChannelMessage(command, channel, data1, data2);
        }

        static SysExMessage ReadSysExMessage(TrackReader reader, SysExType type) {
            int length = ReadVariableLengthQuantity(reader);
            byte[] payload = reader.ReadBytes(length);
            byte[] full = new byte[payload.Length + 1];
            full[0] = (byte)type;
            payload.CopyTo(full, 1);
            return new SysExMessage(full);
        }

        /// <summary>
        /// Preserved legacy quirk (DiVoid #7098 §5.4): discards one byte before the continuation
        /// payload and re-dispatches if a status byte follows. Not exercised by PPQN GM test songs.
        /// </summary>
        static IMidiMessage? ReadSysExContinuation(TrackReader reader, ref byte runningStatus) {
            reader.ReadByte();
            runningStatus = 0;
            byte next = reader.ReadByte();
            if ((next & 0x80) == 0x80)
                return ReadMessage(reader, next, ref runningStatus);
            return ReadSysExMessage(reader, SysExType.Continuation);
        }

        static SysCommonMessage ReadSysCommonMessage(TrackReader reader, byte status, ref byte runningStatus) {
            runningStatus = 0;
            byte data1 = 0;
            byte data2 = 0;
            switch (status) {
                case 0xF1:
                case 0xF3:
                    data1 = reader.ReadByte();
                    break;
                case 0xF2:
                    data1 = reader.ReadByte();
                    data2 = reader.ReadByte();
                    break;
            }
            return new SysCommonMessage((SysCommonMessageType)status, data1, data2);
        }

        static IMidiMessage? ReadSysRealtimeMessage(byte status) {
            switch (status) {
                case 0xF8: return SysRealtimeMessage.ClockMessage;
                case 0xF9: return SysRealtimeMessage.TickMessage;
                case 0xFA: return SysRealtimeMessage.StartMessage;
                case 0xFB: return SysRealtimeMessage.ContinueMessage;
                case 0xFC: return SysRealtimeMessage.StopMessage;
                case 0xFE: return SysRealtimeMessage.ActiveSenseMessage;
                case 0xFF: return SysRealtimeMessage.ResetMessage;
                default: return null;
            }
        }

        static int ReadVariableLengthQuantity(TrackReader reader) {
            int length = reader.ReadByte();
            if ((length & 0x80) == 0x80) {
                length &= 0x7F;
                byte next;
                do {
                    next = reader.ReadByte();
                    length = (length << 7) | (next & 0x7F);
                } while ((next & 0x80) == 0x80);
            }
            return length;
        }

        static string ReadTag(BinaryReader reader) {
            byte[] bytes = reader.ReadBytes(4);
            if (bytes.Length < 4)
                throw new InvalidMidiFileException("Unexpected end of stream while reading a chunk tag.");
            return Encoding.ASCII.GetString(bytes, 0, 4);
        }
    }
}

using System.Collections.Generic;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Fluent assembler for one <c>MTrk</c> chunk's body, used alongside <see cref="MidiTestBuilder"/>
    /// to compose synthetic MIDI files event by event.
    /// </summary>
    internal sealed class MidiTrackEventBuilder {

        readonly List<byte> body = new List<byte>();

        internal MidiTrackEventBuilder Tempo(int deltaTicks, int microsecondsPerQuarterNote) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add(0xFF);
            body.Add(0x51);
            body.Add(0x03);
            body.Add((byte)((microsecondsPerQuarterNote >> 16) & 0xFF));
            body.Add((byte)((microsecondsPerQuarterNote >> 8) & 0xFF));
            body.Add((byte)(microsecondsPerQuarterNote & 0xFF));
            return this;
        }

        internal MidiTrackEventBuilder NoteOn(int deltaTicks, byte channel, byte key, byte velocity) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add((byte)(0x90 | channel));
            body.Add(key);
            body.Add(velocity);
            return this;
        }

        internal MidiTrackEventBuilder NoteOff(int deltaTicks, byte channel, byte key, byte velocity = 0) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add((byte)(0x80 | channel));
            body.Add(key);
            body.Add(velocity);
            return this;
        }

        internal MidiTrackEventBuilder ProgramChange(int deltaTicks, byte channel, byte program) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add((byte)(0xC0 | channel));
            body.Add(program);
            return this;
        }

        internal MidiTrackEventBuilder Controller(int deltaTicks, byte channel, byte controller, byte value) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add((byte)(0xB0 | channel));
            body.Add(controller);
            body.Add(value);
            return this;
        }

        internal MidiTrackEventBuilder PitchWheel(int deltaTicks, byte channel, int value14) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add((byte)(0xE0 | channel));
            body.Add((byte)(value14 & 0x7F));
            body.Add((byte)((value14 >> 7) & 0x7F));
            return this;
        }

        internal MidiTrackEventBuilder EndOfTrack(int deltaTicks = 0) {
            body.AddRange(MidiTestBuilder.EncodeVariableLength(deltaTicks));
            body.Add(0xFF);
            body.Add(0x2F);
            body.Add(0x00);
            return this;
        }

        internal byte[] BuildChunk() {
            return MidiTestBuilder.BuildTrackChunk(body.ToArray());
        }
    }
}

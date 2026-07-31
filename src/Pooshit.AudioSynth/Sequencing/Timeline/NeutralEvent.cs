using System;

namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// A single MIDI-neutral synth-control operation: allocation-free value carried by a
    /// <see cref="TimelineEntry"/>. One factory per <c>ISynthesizer</c> method.
    /// </summary>
    public readonly struct NeutralEvent : IEquatable<NeutralEvent> {

        NeutralEvent(NeutralEventKind kind, int channel, int key, int velocity, int bank, int program, float value, bool held) {
            Kind = kind;
            Channel = channel;
            Key = key;
            Velocity = velocity;
            Bank = bank;
            Program = program;
            Value = value;
            Held = held;
        }

        /// <summary>The control operation this event performs.</summary>
        public NeutralEventKind Kind { get; }

        /// <summary>The target MIDI channel.</summary>
        public int Channel { get; }

        /// <summary><see cref="NeutralEventKind.NoteOn"/>/<see cref="NeutralEventKind.NoteOff"/> key.</summary>
        public int Key { get; }

        /// <summary><see cref="NeutralEventKind.NoteOn"/> velocity.</summary>
        public int Velocity { get; }

        /// <summary><see cref="NeutralEventKind.SetPatch"/> resolved bank number.</summary>
        public int Bank { get; }

        /// <summary><see cref="NeutralEventKind.SetPatch"/> program number.</summary>
        public int Program { get; }

        /// <summary>Scalar payload for gain/pan/pitch-bend/modulation/reverb/chorus events.</summary>
        public float Value { get; }

        /// <summary><see cref="NeutralEventKind.SetSustain"/> pedal state.</summary>
        public bool Held { get; }

        /// <summary>Creates a <see cref="NeutralEventKind.NoteOn"/> event.</summary>
        public static NeutralEvent NoteOn(int channel, int key, int velocity) =>
            new NeutralEvent(NeutralEventKind.NoteOn, channel, key, velocity, 0, 0, 0f, false);

        /// <summary>Creates a <see cref="NeutralEventKind.NoteOff"/> event.</summary>
        public static NeutralEvent NoteOff(int channel, int key) =>
            new NeutralEvent(NeutralEventKind.NoteOff, channel, key, 0, 0, 0, 0f, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetPatch"/> event with a resolved (bank, program).</summary>
        public static NeutralEvent SetPatch(int channel, int bank, int program) =>
            new NeutralEvent(NeutralEventKind.SetPatch, channel, 0, 0, bank, program, 0f, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetGain"/> event.</summary>
        public static NeutralEvent SetGain(int channel, float gain) =>
            new NeutralEvent(NeutralEventKind.SetGain, channel, 0, 0, 0, 0, gain, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetPan"/> event.</summary>
        public static NeutralEvent SetPan(int channel, float pan) =>
            new NeutralEvent(NeutralEventKind.SetPan, channel, 0, 0, 0, 0, pan, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetPitchBend"/> event.</summary>
        public static NeutralEvent SetPitchBend(int channel, float semitones) =>
            new NeutralEvent(NeutralEventKind.SetPitchBend, channel, 0, 0, 0, 0, semitones, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetModulation"/> event.</summary>
        public static NeutralEvent SetModulation(int channel, float amount) =>
            new NeutralEvent(NeutralEventKind.SetModulation, channel, 0, 0, 0, 0, amount, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetReverbSend"/> event.</summary>
        public static NeutralEvent SetReverbSend(int channel, float level) =>
            new NeutralEvent(NeutralEventKind.SetReverbSend, channel, 0, 0, 0, 0, level, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetChorusSend"/> event.</summary>
        public static NeutralEvent SetChorusSend(int channel, float level) =>
            new NeutralEvent(NeutralEventKind.SetChorusSend, channel, 0, 0, 0, 0, level, false);

        /// <summary>Creates a <see cref="NeutralEventKind.SetSustain"/> event.</summary>
        public static NeutralEvent SetSustain(int channel, bool held) =>
            new NeutralEvent(NeutralEventKind.SetSustain, channel, 0, 0, 0, 0, 0f, held);

        /// <summary>Creates a <see cref="NeutralEventKind.SilenceChannel"/> event.</summary>
        public static NeutralEvent SilenceChannel(int channel) =>
            new NeutralEvent(NeutralEventKind.SilenceChannel, channel, 0, 0, 0, 0, 0f, false);

        /// <summary>Creates a <see cref="NeutralEventKind.ReleaseAllNotes"/> event.</summary>
        public static NeutralEvent ReleaseAllNotes(int channel) =>
            new NeutralEvent(NeutralEventKind.ReleaseAllNotes, channel, 0, 0, 0, 0, 0f, false);

        /// <inheritdoc/>
        public bool Equals(NeutralEvent other) =>
            Kind == other.Kind && Channel == other.Channel && Key == other.Key && Velocity == other.Velocity
            && Bank == other.Bank && Program == other.Program && Value.Equals(other.Value) && Held == other.Held;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is NeutralEvent other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() {
            unchecked {
                int hash = (int)Kind;
                hash = (hash * 397) ^ Channel;
                hash = (hash * 397) ^ Key;
                hash = (hash * 397) ^ Velocity;
                hash = (hash * 397) ^ Bank;
                hash = (hash * 397) ^ Program;
                hash = (hash * 397) ^ Value.GetHashCode();
                hash = (hash * 397) ^ Held.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc/>
        public override string ToString() => $"{Kind}(ch={Channel})";
    }
}

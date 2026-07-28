namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// One entry in the synthesizer's fixed voice pool; tracks occupancy, the originating channel
    /// and key for note-off matching, and the active voice instance.
    /// </summary>
    internal struct VoiceSlot {

        /// <summary>
        /// True when this slot holds an active or releasing voice.
        /// </summary>
        public bool IsOccupied;

        /// <summary>
        /// MIDI channel of the note that allocated this slot.
        /// </summary>
        public int Channel;

        /// <summary>
        /// MIDI key number of the note that allocated this slot.
        /// </summary>
        public int Key;

        /// <summary>
        /// The voice rendering in this slot; null when <see cref="IsOccupied"/> is false.
        /// </summary>
        public IVoice? Voice;

        /// <summary>
        /// NoteOff deferred by a held sustain pedal: true once this slot's voice has received a
        /// <see cref="ISynthesizer.NoteOff"/> while the channel's sustain pedal was down, meaning it
        /// still sounds and must release when the pedal lifts. Reset to false whenever a slot is
        /// (re)allocated by <see cref="ISynthesizer.NoteOn"/>.
        /// </summary>
        public bool PendingRelease;

        /// <summary>
        /// Monotonic stamp assigned when a note starts in this slot (ordinary allocation or a stolen
        /// slot's deferred pending-note start); lower means older. Used only to break ties toward the
        /// oldest note when the voice-stealing victim selection compares equally-released, equally-quiet
        /// candidates.
        /// </summary>
        public int Age;

        /// <summary>
        /// True once this slot's note has actually been let go — an immediate <see cref="ISynthesizer.NoteOff"/>
        /// (not deferred by sustain), or a sustain-pedal lift that released a note previously deferred via
        /// <see cref="PendingRelease"/>. Distinct from <see cref="PendingRelease"/> (key up but still held
        /// by the pedal, still at full level); for voice-stealing purposes either one marks the slot as a
        /// released-tier victim, preferred over a still-held, sounding voice. Reset to false whenever a
        /// slot is (re)allocated by <see cref="ISynthesizer.NoteOn"/>.
        /// </summary>
        public bool Released;

        /// <summary>
        /// MIDI channel of a note deferred behind this slot's declick fade-out while its voice is being
        /// stolen; <c>-1</c> means no note is pending. Set when the engine steals this slot and cleared
        /// (reset to <c>-1</c>) whenever the slot starts sounding a note, whether via ordinary allocation
        /// or by consuming its own pending note once the outgoing voice reaches silence.
        /// </summary>
        public int PendingChannel;

        /// <summary>
        /// MIDI key of the note deferred behind this slot's declick fade-out; meaningful only while
        /// <see cref="PendingChannel"/> is not <c>-1</c>.
        /// </summary>
        public int PendingKey;

        /// <summary>
        /// MIDI velocity of the note deferred behind this slot's declick fade-out; meaningful only while
        /// <see cref="PendingChannel"/> is not <c>-1</c>.
        /// </summary>
        public int PendingVelocity;
    }
}

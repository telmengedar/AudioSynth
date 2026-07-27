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
    }
}

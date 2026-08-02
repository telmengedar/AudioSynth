namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// One instrument-table slot: a symbolic mapping from a tracker instrument to a SoundBank
    /// <c>(bank, program)</c> patch, plus an editor-facing label. Holds no audio data and no engine
    /// handle — the bound SoundBank owns the actual patch.
    /// </summary>
    public struct Instrument {

        /// <summary>SoundBank bank number this instrument selects.</summary>
        public int Bank { get; set; }

        /// <summary>SoundBank program number this instrument selects.</summary>
        public int Program { get; set; }

        /// <summary>Optional human-readable label for editors; not read by the importer.</summary>
        public string? Name { get; set; }
    }
}

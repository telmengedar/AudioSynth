namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Descriptor of one loadable patch as exposed by <see cref="SoundBank.AvailablePatches"/>: the
    /// (bank, program) address <see cref="SoundBank.GetPatch"/> resolves and its display name.
    /// </summary>
    public readonly struct PatchInfo {

        /// <summary>
        /// Creates a patch descriptor for the given bank, program and name.
        /// </summary>
        public PatchInfo(int bank, int program, string name) {
            Bank = bank;
            Program = program;
            Name = name;
        }

        /// <summary>
        /// MIDI bank number (0-127; 128 = percussion).
        /// </summary>
        public int Bank { get; }

        /// <summary>
        /// MIDI program/patch number within <see cref="Bank"/>.
        /// </summary>
        public int Program { get; }

        /// <summary>
        /// Preset name as parsed from the source bank; may be empty for a blank/malformed entry.
        /// </summary>
        public string Name { get; }
    }
}

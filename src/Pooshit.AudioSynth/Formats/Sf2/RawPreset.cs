namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>Parsed phdr record: a preset's identity plus the index of its first zone bag.</summary>
    internal readonly struct RawPreset {

        /// <summary>Creates a parsed preset-header record from raw phdr fields.</summary>
        public RawPreset(string name, ushort patchNumber, ushort bankNumber, ushort bagIndex) {
            Name = name;
            PatchNumber = patchNumber;
            BankNumber = bankNumber;
            BagIndex = bagIndex;
        }

        /// <summary>Preset name as stored in the phdr record.</summary>
        public string Name { get; }

        /// <summary>MIDI program (patch) number the preset responds to.</summary>
        public ushort PatchNumber { get; }

        /// <summary>Bank number the preset belongs to.</summary>
        public ushort BankNumber { get; }

        /// <summary>Index of the preset's first zone bag within the pbag list.</summary>
        public ushort BagIndex { get; }
    }
}

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One SF2 preset record (SF2 section 7.2/7.3): a named entry in the phdr sub-chunk that maps
    /// a bank+patch number to a set of preset zones, each of which selects an instrument.
    /// </summary>
    public sealed class Sf2PresetHeader {

        /// <summary>
        /// Creates an <see cref="Sf2PresetHeader"/>.
        /// </summary>
        public Sf2PresetHeader(string name, int patchNumber, int bankNumber, Sf2Zone[] zones) {
            Name = name;
            PatchNumber = patchNumber;
            BankNumber = bankNumber;
            Zones = zones;
        }

        /// <summary>
        /// Preset name as stored in the SF2 achPresetName field (up to 20 ASCII characters).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// MIDI program number (0–127) or percussion bank index for bank 128.
        /// </summary>
        public int PatchNumber { get; }

        /// <summary>
        /// MIDI bank number (0–127; 128 = percussion).
        /// </summary>
        public int BankNumber { get; }

        /// <summary>
        /// Preset zones; each zone selects an instrument and may apply additional generators.
        /// </summary>
        public Sf2Zone[] Zones { get; }

        /// <inheritdoc/>
        public override string ToString() => $"{BankNumber}-{PatchNumber} {Name}";
    }
}

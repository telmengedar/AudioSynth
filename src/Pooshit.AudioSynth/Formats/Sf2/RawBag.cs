namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>Parsed pbag/ibag record: the starting generator and modulator indices of one zone.</summary>
    internal readonly struct RawBag {

        /// <summary>Creates a parsed zone-bag record from raw pbag/ibag fields.</summary>
        public RawBag(ushort generatorIndex, ushort modulatorIndex) {
            GeneratorIndex = generatorIndex;
            ModulatorIndex = modulatorIndex;
        }

        /// <summary>Index of the zone's first generator within the pgen/igen list.</summary>
        public ushort GeneratorIndex { get; }

        /// <summary>Index of the zone's first modulator within the pmod/imod list.</summary>
        public ushort ModulatorIndex { get; }
    }
}

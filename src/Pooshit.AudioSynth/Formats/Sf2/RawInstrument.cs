namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>Parsed inst record: an instrument's name plus the index of its first zone bag.</summary>
    internal readonly struct RawInstrument {

        /// <summary>Creates a parsed instrument-header record from raw inst fields.</summary>
        public RawInstrument(string name, ushort bagIndex) {
            Name = name;
            BagIndex = bagIndex;
        }

        /// <summary>Instrument name as stored in the inst record.</summary>
        public string Name { get; }

        /// <summary>Index of the instrument's first zone bag within the ibag list.</summary>
        public ushort BagIndex { get; }
    }
}

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One SF2 instrument record (SF2 section 7.6/7.7): a named collection of instrument zones that
    /// each map a sample to a key/velocity region.
    /// </summary>
    public sealed class Sf2Instrument {

        /// <summary>
        /// Creates an <see cref="Sf2Instrument"/>.
        /// </summary>
        public Sf2Instrument(string name, Sf2Zone[] zones) {
            Name = name;
            Zones = zones;
        }

        /// <summary>
        /// Instrument name as stored in the SF2 achInstName field (up to 20 ASCII characters).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Instrument zones; each zone maps a sample to a key/velocity region and adds generators.
        /// </summary>
        public Sf2Zone[] Zones { get; }

        /// <inheritdoc/>
        public override string ToString() => Name;
    }
}

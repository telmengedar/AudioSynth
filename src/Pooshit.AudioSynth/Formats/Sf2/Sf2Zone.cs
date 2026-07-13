namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One SF2 zone: a set of generators and modulators drawn from the pdta bag + gen + mod chunks.
    /// Zones appear in both preset headers (each describes which instrument to use) and instruments
    /// (each describes which sample to use and how to shape the voice).
    /// </summary>
    public sealed class Sf2Zone {

        /// <summary>
        /// Creates an <see cref="Sf2Zone"/>.
        /// </summary>
        public Sf2Zone(Sf2Generator[] generators, Sf2Modulator[] modulators) {
            Generators = generators;
            Modulators = modulators;
        }

        /// <summary>
        /// Parameter generators for this zone, ordered as stored in the file.
        /// </summary>
        public Sf2Generator[] Generators { get; }

        /// <summary>
        /// Modulators that route real-time controllers to generators for this zone.
        /// </summary>
        public Sf2Modulator[] Modulators { get; }

        /// <inheritdoc/>
        public override string ToString() =>
            $"Zone(gens={Generators.Length}, mods={Modulators.Length})";
    }
}

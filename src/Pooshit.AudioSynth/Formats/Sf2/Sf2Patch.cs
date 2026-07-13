using System;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// An SF2 preset exposed through the <see cref="IPatch"/> seam.  Carries the parsed
    /// <see cref="Sf2PresetHeader"/>, the shared sample-data pool, and all sample headers so that a
    /// future voice engine can look up the right sample for a given key and velocity.
    /// </summary>
    public sealed class Sf2Patch : IPatch {

        /// <summary>
        /// Creates an <see cref="Sf2Patch"/>.
        /// </summary>
        public Sf2Patch(
            Sf2PresetHeader preset,
            Sf2Instrument[] instruments,
            Sf2SampleHeader[] sampleHeaders,
            Sf2SampleData sampleData) {
            Preset = preset ?? throw new ArgumentNullException(nameof(preset));
            Instruments = instruments ?? throw new ArgumentNullException(nameof(instruments));
            SampleHeaders = sampleHeaders ?? throw new ArgumentNullException(nameof(sampleHeaders));
            SampleData = sampleData ?? throw new ArgumentNullException(nameof(sampleData));
        }

        /// <summary>
        /// The parsed SF2 preset header (bank, patch number, zones).
        /// </summary>
        public Sf2PresetHeader Preset { get; }

        /// <summary>
        /// All instruments in the bank (needed by the voice engine to resolve preset zones
        /// to instrument zones and from there to sample headers).
        /// </summary>
        public Sf2Instrument[] Instruments { get; }

        /// <summary>
        /// All sample headers from the shdr sub-chunk; shared across all patches from the same file.
        /// </summary>
        public Sf2SampleHeader[] SampleHeaders { get; }

        /// <summary>
        /// The raw sample-data pool shared across all patches from the same file.
        /// </summary>
        public Sf2SampleData SampleData { get; }

        /// <summary>
        /// Not implemented in the loader PR — the voice engine (a future PR) implements this.
        /// </summary>
        /// <exception cref="NotImplementedException">Always thrown until the voice engine lands.</exception>
        public IVoice StartVoice(int key, int velocity) =>
            throw new NotImplementedException(
                "Voice engine is a future PR; Sf2Patch.StartVoice is not yet implemented.");

        /// <inheritdoc/>
        public override string ToString() => Preset.ToString();
    }
}

using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// An SF2 preset exposed through the <see cref="IPatch"/> seam.  Carries the parsed
    /// <see cref="Sf2PresetHeader"/>, the shared sample-data pool, and all sample headers.
    /// <see cref="StartVoice"/> resolves preset → instrument → zone → sample and returns a live
    /// voice, or an inactive no-op voice when no zone covers the note.
    /// </summary>
    public sealed class Sf2Patch : IPatch {

        readonly int outputSampleRate;
        readonly Sf2RegionResolver resolver;
        readonly Dictionary<long, SamplePatch> regionCache;

        /// <summary>
        /// Creates an <see cref="Sf2Patch"/>.
        /// </summary>
        /// <param name="preset">the parsed SF2 preset header (bank, patch number, zones)</param>
        /// <param name="instruments">all instruments in the bank</param>
        /// <param name="sampleHeaders">all sample headers from the shdr sub-chunk</param>
        /// <param name="sampleData">raw sample-data pool shared across all patches from the same file</param>
        /// <param name="outputSampleRate">engine output sample rate; used to compute pitch increments</param>
        public Sf2Patch(
            Sf2PresetHeader preset,
            Sf2Instrument[] instruments,
            Sf2SampleHeader[] sampleHeaders,
            Sf2SampleData sampleData,
            int outputSampleRate = 44100) {
            Preset = preset ?? throw new ArgumentNullException(nameof(preset));
            Instruments = instruments ?? throw new ArgumentNullException(nameof(instruments));
            SampleHeaders = sampleHeaders ?? throw new ArgumentNullException(nameof(sampleHeaders));
            SampleData = sampleData ?? throw new ArgumentNullException(nameof(sampleData));
            if (outputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            this.outputSampleRate = outputSampleRate;
            resolver = new Sf2RegionResolver(preset, instruments, sampleHeaders, sampleData.FloatPool);
            regionCache = new Dictionary<long, SamplePatch>();
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
        /// Resolves the SF2 preset → instrument → zone → sample chain for the given key and velocity
        /// and returns a <see cref="SamplePlaybackVoice"/> ready to render, or an inactive no-op voice
        /// when no instrument zone covers the note.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        public IVoice StartVoice(int key, int velocity) {
            if (!resolver.TryResolve(key, velocity, out SampleRegion? region, out long cacheKey))
                return InactiveVoice.Instance;

            if (!regionCache.TryGetValue(cacheKey, out SamplePatch? patch)) {
                patch = new SamplePatch(region!, outputSampleRate);
                regionCache[cacheKey] = patch;
            }

            return patch.StartVoice(key, velocity);
        }

        /// <inheritdoc/>
        public override string ToString() => Preset.ToString();
    }
}

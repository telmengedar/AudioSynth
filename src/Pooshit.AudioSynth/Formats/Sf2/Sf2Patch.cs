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
    /// voice, or an inactive no-op voice when no zone covers the note. Also implements
    /// <see cref="IMultiVoicePatch"/> (<see cref="StartVoices"/>): SF2 zone/layer stacking (DiVoid
    /// #7282) — starts one voice per covering (preset zone, instrument zone) pair, up to
    /// <see cref="MaxLayersPerNote"/>, so overlapping zones (e.g. a tonal zone plus a near-0-cB
    /// attack/click companion zone) sound together instead of only the first match.
    /// </summary>
    public sealed class Sf2Patch : IMultiVoicePatch {

        /// <summary>
        /// Upper bound on simultaneous layers started for one note-on, applied when materialising
        /// <see cref="Sf2RegionResolver.ResolveAll"/>'s layer set (the first N in resolver order are
        /// kept). SF2 imposes no cap, but an unbounded preset-zone × instrument-zone cartesian could, on
        /// a pathological font, request many voices per note and starve polyphony; OmegaGMGS2's real
        /// overlapping-zone count per note is ~2–3, so 4 is comfortable headroom (DiVoid #7282 §9.3,
        /// #7283 locked decision 1).
        /// </summary>
        const int MaxLayersPerNote = 4;

        readonly int outputSampleRate;
        readonly Sf2RegionResolver resolver;
        readonly Dictionary<long, SamplePatch> regionCache;
        readonly List<Sf2ResolvedLayer> layerBuffer;

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
            layerBuffer = new List<Sf2ResolvedLayer>(MaxLayersPerNote);
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

            SamplePatch patch = GetOrCreateRegionPatch(cacheKey, region!);
            return patch.StartVoice(key, velocity);
        }

        /// <summary>
        /// Starts every voice needed for one note-on: resolves ALL covering (preset zone, instrument
        /// zone) pairs via <see cref="Sf2RegionResolver.ResolveAll"/> (SF2 zone/layer stacking, DiVoid
        /// #7282), caps the layer set at <see cref="MaxLayersPerNote"/> (emitting the first N in
        /// resolver order), and appends one live voice per kept layer to <paramref name="voices"/>. A
        /// preset with no covering zone appends nothing, matching <see cref="StartVoice"/>'s
        /// <see cref="InactiveVoice"/> no-match case but without occupying an engine slot for it.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <param name="voices">caller-owned, caller-cleared buffer that layers are appended to</param>
        public void StartVoices(int key, int velocity, List<IVoice> voices) {
            layerBuffer.Clear();
            int layerCount = resolver.ResolveAll(key, velocity, layerBuffer);
            if (layerCount > MaxLayersPerNote)
                layerCount = MaxLayersPerNote;

            for (int i = 0; i < layerCount; i++) {
                Sf2ResolvedLayer layer = layerBuffer[i];
                SamplePatch patch = GetOrCreateRegionPatch(layer.CacheKey, layer.Region);
                voices.Add(patch.StartVoice(key, velocity));
            }
        }

        /// <summary>
        /// Looks up (or creates and caches) the <see cref="SamplePatch"/> for a resolved region's cache
        /// key, shared by <see cref="StartVoice"/> and <see cref="StartVoices"/> so both single-match and
        /// stacked resolution reuse the same per-region cache.
        /// </summary>
        SamplePatch GetOrCreateRegionPatch(long cacheKey, SampleRegion region) {
            if (!regionCache.TryGetValue(cacheKey, out SamplePatch? patch)) {
                patch = new SamplePatch(region, outputSampleRate);
                regionCache[cacheKey] = patch;
            }
            return patch;
        }

        /// <inheritdoc/>
        public override string ToString() => Preset.ToString();
    }
}

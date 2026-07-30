using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One entry produced by <see cref="Sf2RegionResolver.ResolveAll"/>: a resolved <see cref="SampleRegion"/>
    /// paired with the opaque cache key identifying the exact (preset zone, instrument zone) pair it came
    /// from, so <see cref="Sf2Patch"/> can cache one <c>SamplePatch</c> per distinct pair across note-ons
    /// (SF2 zone/layer stacking, DiVoid #7282 §7).
    /// </summary>
    public readonly struct Sf2ResolvedLayer {

        /// <summary>
        /// Creates an <see cref="Sf2ResolvedLayer"/>.
        /// </summary>
        /// <param name="region">the resolved sample region for this layer</param>
        /// <param name="cacheKey">opaque cache key for the (preset zone, instrument zone) pair that produced <paramref name="region"/></param>
        public Sf2ResolvedLayer(SampleRegion region, long cacheKey) {
            Region = region;
            CacheKey = cacheKey;
        }

        /// <summary>
        /// The resolved sample region for this layer.
        /// </summary>
        public SampleRegion Region { get; }

        /// <summary>
        /// Opaque cache key identifying the (preset zone, instrument zone) pair that produced <see cref="Region"/>.
        /// </summary>
        public long CacheKey { get; }
    }
}

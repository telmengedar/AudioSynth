using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.RenderDemo {

    /// <summary>
    /// Demo-only <see cref="IPatch"/> decorator that re-resolves the wrapped SF2 patch's region for
    /// each note and overrides its <see cref="LfoParameters"/> with a caller-supplied rate and the three
    /// routed depths (pitch/vibrato, volume/tremolo, filter-cutoff/sweep), so the render demo can produce
    /// audible A/B proofs without needing a preset that specifies a mod-LFO.  Not part of the library;
    /// exists only to drive the deliverable proof renders.
    /// </summary>
    sealed class ModLfoOverridePatch : IPatch {

        readonly Sf2Patch source;
        readonly Sf2RegionResolver resolver;
        readonly int outputSampleRate;
        readonly LfoParameters lfo;

        /// <summary>
        /// Creates a <see cref="ModLfoOverridePatch"/>.
        /// </summary>
        /// <param name="source">the loaded SF2 patch whose regions are re-resolved and overridden</param>
        /// <param name="outputSampleRate">engine output sample rate used to compute the pitch increment</param>
        /// <param name="rateHz">mod-LFO oscillation frequency in hertz</param>
        /// <param name="pitchDepthCents">peak vibrato pitch deviation, in cents, at full LFO excursion</param>
        /// <param name="volumeDepthCentibels">peak tremolo volume deviation, in centibels, at full LFO excursion</param>
        /// <param name="filterDepthCents">peak filter-sweep cutoff deviation, in cents, at full LFO excursion</param>
        public ModLfoOverridePatch(
            Sf2Patch source,
            int outputSampleRate,
            float rateHz,
            float pitchDepthCents,
            float volumeDepthCentibels,
            float filterDepthCents) {
            this.source = source;
            resolver = new Sf2RegionResolver(source.Preset, source.Instruments, source.SampleHeaders, source.SampleData.FloatPool);
            this.outputSampleRate = outputSampleRate;
            lfo = new LfoParameters(0f, rateHz, pitchDepthCents, volumeDepthCentibels, filterDepthCents);
        }

        /// <summary>
        /// Resolves the note as <see cref="Sf2Patch"/> would, then starts a voice from a region carrying
        /// the overridden mod-LFO in place of the preset's own.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        public IVoice StartVoice(int key, int velocity) {
            if (!resolver.TryResolve(key, velocity, out SampleRegion? region, out _))
                return source.StartVoice(key, velocity);

            SampleRegion overridden = new SampleRegion(
                region!.Buffer, region.Start, region.End, region.LoopStart, region.LoopEnd, region.LoopMode,
                region.SourceSampleRate, region.RootKey, region.PitchCorrectionCents, region.Envelope, region.Filter,
                lfo);

            SamplePatch patch = new SamplePatch(overridden, outputSampleRate);
            return patch.StartVoice(key, velocity);
        }
    }
}

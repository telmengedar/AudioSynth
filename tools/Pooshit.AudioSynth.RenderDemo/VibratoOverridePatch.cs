using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.RenderDemo {

    /// <summary>
    /// Demo-only <see cref="IPatch"/> decorator that re-resolves the wrapped SF2 patch's region for
    /// each note and overrides its <see cref="LfoParameters"/> with a caller-supplied vibrato rate and
    /// pitch depth, so the render demo can produce an audible vibrato A/B without needing a preset that
    /// specifies a mod-LFO.  Not part of the library; exists only to drive the deliverable proof render.
    /// </summary>
    sealed class VibratoOverridePatch : IPatch {

        readonly Sf2Patch source;
        readonly Sf2RegionResolver resolver;
        readonly int outputSampleRate;
        readonly LfoParameters vibrato;

        /// <summary>
        /// Creates a <see cref="VibratoOverridePatch"/>.
        /// </summary>
        /// <param name="source">the loaded SF2 patch whose regions are re-resolved and overridden</param>
        /// <param name="outputSampleRate">engine output sample rate used to compute the pitch increment</param>
        /// <param name="rateHz">vibrato oscillation frequency in hertz</param>
        /// <param name="depthCents">peak vibrato pitch deviation, in cents, at full LFO excursion</param>
        public VibratoOverridePatch(Sf2Patch source, int outputSampleRate, float rateHz, float depthCents) {
            this.source = source;
            resolver = new Sf2RegionResolver(source.Preset, source.Instruments, source.SampleHeaders, source.SampleData.FloatPool);
            this.outputSampleRate = outputSampleRate;
            vibrato = new LfoParameters(0f, rateHz, depthCents);
        }

        /// <summary>
        /// Resolves the note as <see cref="Sf2Patch"/> would, then starts a voice from a region carrying
        /// the overridden vibrato LFO in place of the preset's own.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        public IVoice StartVoice(int key, int velocity) {
            if (!resolver.TryResolve(key, velocity, out SampleRegion? region, out _))
                return source.StartVoice(key, velocity);

            SampleRegion overridden = new SampleRegion(
                region!.Buffer, region.Start, region.End, region.LoopStart, region.LoopEnd, region.LoopMode,
                region.SourceSampleRate, region.RootKey, region.PitchCorrectionCents, region.Envelope, region.Filter,
                vibrato);

            SamplePatch patch = new SamplePatch(overridden, outputSampleRate);
            return patch.StartVoice(key, velocity);
        }
    }
}

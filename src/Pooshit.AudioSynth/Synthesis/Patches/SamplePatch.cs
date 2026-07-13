using System;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Synthesis.Patches {

    /// <summary>
    /// <see cref="IPatch"/> that plays a fixed mono <see cref="SampleRegion"/>; <see cref="StartVoice"/>
    /// computes the pitch increment for the played key and a linear velocity-to-gain mapping, then
    /// constructs a <see cref="SamplePlaybackVoice"/> ready to render.
    /// </summary>
    public sealed class SamplePatch : IPatch {

        readonly SampleRegion _region;
        readonly int _outputSampleRate;

        /// <summary>
        /// Creates a <see cref="SamplePatch"/>.
        /// </summary>
        /// <param name="region">the sample region to play for every note</param>
        /// <param name="outputSampleRate">engine output sample rate used to compute the pitch increment</param>
        public SamplePatch(SampleRegion region, int outputSampleRate) {
            _region = region ?? throw new ArgumentNullException(nameof(region));
            if (outputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            _outputSampleRate = outputSampleRate;
        }

        /// <summary>
        /// Starts a <see cref="SamplePlaybackVoice"/> for the given note and velocity.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127); mapped linearly to gain</param>
        public IVoice StartVoice(int key, int velocity) {
            double semitones = (key - _region.RootKey) + _region.PitchCorrectionCents / 100.0;
            float pitchIncrement = (float)(Math.Pow(2.0, semitones / 12.0) * _region.SourceSampleRate / (double)_outputSampleRate);
            float targetGain = velocity / 127f;
            return new SamplePlaybackVoice(_region, pitchIncrement, targetGain, _outputSampleRate);
        }
    }
}

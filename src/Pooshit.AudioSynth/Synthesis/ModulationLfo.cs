using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Per-voice modulation low-frequency oscillator: produces a delayed, periodic, bipolar [-1, 1]
    /// triangle signal at the rate described by an <see cref="LfoParameters"/> descriptor.  It does not
    /// know about pitch, gain, or filtering; routing the signal is the caller's concern.  It is a
    /// mutable struct advanced in place, like <see cref="AmplitudeEnvelope"/> and
    /// <see cref="BiquadLowPassFilter"/>; copying it by value loses the in-flight phase.  The triangle
    /// waveform starts at zero rising and needs no transcendental to evaluate, so <see cref="Advance"/>
    /// is a caller-supplied-frame-count multiply/compare step; block size is never an input (INV-1).
    /// Zero pitch depth is treated as inert (bypass): the phase never advances and the value stays a
    /// constant zero, matching <see cref="LfoParameters.Default"/>.
    /// </summary>
    public struct ModulationLfo {

        readonly bool bypass;
        readonly double phaseIncrementPerFrame;

        int delayFramesRemaining;
        double phase;

        /// <summary>
        /// Creates a <see cref="ModulationLfo"/> positioned at the start of its delay, with phase zero.
        /// </summary>
        /// <param name="parameters">the rate-independent delay, frequency and pitch depth for this note</param>
        /// <param name="sampleRate">output sample rate, used to convert delay seconds and frequency into per-frame terms</param>
        public ModulationLfo(LfoParameters parameters, int sampleRate) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            bypass = parameters.PitchDepthCents == 0f;
            phaseIncrementPerFrame = bypass ? 0.0 : parameters.FrequencyHz / sampleRate;
            delayFramesRemaining = bypass ? 0 : FramesFromSeconds(parameters.DelaySeconds, sampleRate);
            phase = 0.0;
        }

        /// <summary>
        /// Advances the LFO by <paramref name="frames"/> output frames and returns the bipolar value
        /// after the advance; zero throughout the delay and whenever the LFO is inert.
        /// </summary>
        /// <param name="frames">number of output frames elapsed since the previous call</param>
        public float Advance(int frames) {
            if (bypass)
                return 0f;

            if (delayFramesRemaining > 0) {
                int consumed = Math.Min(delayFramesRemaining, frames);
                delayFramesRemaining -= consumed;
                frames -= consumed;
                if (frames <= 0)
                    return 0f;
            }

            phase += phaseIncrementPerFrame * frames;
            phase -= Math.Floor(phase);
            return TriangleFromPhase(phase);
        }

        static float TriangleFromPhase(double phase) {
            if (phase < 0.25)
                return (float)(phase * 4.0);
            if (phase < 0.75)
                return (float)(2.0 - phase * 4.0);
            return (float)(phase * 4.0 - 4.0);
        }

        static int FramesFromSeconds(float seconds, int sampleRate) {
            if (seconds <= 0f)
                return 0;
            double frames = Math.Round((double)seconds * sampleRate);
            if (frames < 0d)
                return 0;
            return (int)frames;
        }
    }
}

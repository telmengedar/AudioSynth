using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Stereo Schroeder/Freeverb-style reverb: a mono send feeds two parallel banks of eight damped
    /// <see cref="CombFilter"/>s, each bank's sum passing through a series of four <see cref="AllpassFilter"/>s;
    /// the right bank's delay lengths are offset from the left's by <see cref="StereoSpreadSamples"/> so the
    /// two channels decorrelate, giving width even though both banks share one mono send. All delay-line
    /// buffers are sized from the sample rate and allocated in the constructor, so <see cref="Process"/> is
    /// allocation-free and safe to call from <see cref="Synthesizer.Read"/>'s steady state. Feedback is
    /// bounded strictly below 1 by <see cref="ReverbSettings"/>'s construction-time clamp and the in-feedback
    /// damping low-pass dissipates energy every pass, so the reverb is BIBO-stable by construction — it never
    /// relies on the master soft-clip or <see cref="Synthesizer"/>'s NaN/Inf guard (INV-2) to stay bounded.
    /// Dry gain is fixed at 1.0, so <see cref="ReverbSettings.Wet"/> = 0 mixes <c>dry·1.0 + wet·0.0</c> — a
    /// structural, float-exact passthrough.
    /// </summary>
    public sealed class Reverb {

        /// <summary>Reference sample rate the Freeverb delay-line tunings below are defined at.</summary>
        const int ReferenceSampleRate = 44100;

        /// <summary>Samples added to every left-bank length to build the corresponding right-bank length.</summary>
        const int StereoSpreadSamples = 23;

        /// <summary>Mono-send gain applied to <c>(L + R)</c> before it feeds either comb bank.</summary>
        const float InputGain = 0.015f;

        /// <summary>Comb delay-line lengths (samples, at <see cref="ReferenceSampleRate"/>) for the left bank.</summary>
        static readonly int[] CombTuningsL = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };

        /// <summary>Allpass delay-line lengths (samples, at <see cref="ReferenceSampleRate"/>) for the left chain.</summary>
        static readonly int[] AllpassTuningsL = { 556, 441, 341, 225 };

        readonly CombFilter[] combL;
        readonly CombFilter[] combR;
        readonly AllpassFilter[] allpassL;
        readonly AllpassFilter[] allpassR;
        readonly float wet1;
        readonly float wet2;

        /// <summary>
        /// Creates a <see cref="Reverb"/> for <paramref name="sampleRate"/>, allocating every comb and
        /// allpass delay line up front: Freeverb's 44.1 kHz tunings scaled by <c>sampleRate/44100</c> and
        /// floored to at least 1 sample, so <see cref="Process"/> allocates nothing.
        /// </summary>
        /// <param name="settings">room size, damping, wet and width; already stability-clamped</param>
        /// <param name="sampleRate">output sample rate in frames per second; must be positive</param>
        public Reverb(ReverbSettings settings, int sampleRate) {
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");

            double scale = (double)sampleRate / ReferenceSampleRate;

            combL = new CombFilter[CombTuningsL.Length];
            combR = new CombFilter[CombTuningsL.Length];
            for (int i = 0; i < CombTuningsL.Length; i++) {
                int lengthL = ScaledLength(CombTuningsL[i], scale);
                int lengthR = ScaledLength(CombTuningsL[i] + StereoSpreadSamples, scale);
                combL[i] = new CombFilter(lengthL, settings.Feedback, settings.Damping);
                combR[i] = new CombFilter(lengthR, settings.Feedback, settings.Damping);
            }

            allpassL = new AllpassFilter[AllpassTuningsL.Length];
            allpassR = new AllpassFilter[AllpassTuningsL.Length];
            for (int i = 0; i < AllpassTuningsL.Length; i++) {
                int lengthL = ScaledLength(AllpassTuningsL[i], scale);
                int lengthR = ScaledLength(AllpassTuningsL[i] + StereoSpreadSamples, scale);
                allpassL[i] = new AllpassFilter(lengthL);
                allpassR[i] = new AllpassFilter(lengthR);
            }

            wet1 = settings.Wet * (settings.Width / 2f + 0.5f);
            wet2 = settings.Wet * ((1f - settings.Width) / 2f);
        }

        /// <summary>
        /// Processes an interleaved stereo block in place: each <c>[L, R]</c> frame becomes
        /// <c>[L + wetL, R + wetR]</c>, dry gain fixed at 1.0. Allocation-free; the comb/allpass delay
        /// lines carry state across calls, so the decaying tail spans block boundaries.
        /// </summary>
        /// <param name="block">interleaved stereo samples; length must be a multiple of 2</param>
        public void Process(Span<float> block) {
            if (block.Length % 2 != 0)
                throw new ArgumentException($"block length ({block.Length}) must be a multiple of 2 (interleaved stereo).", nameof(block));

            for (int i = 0; i < block.Length; i += 2) {
                float left = block[i];
                float right = block[i + 1];
                float input = (left + right) * InputGain;

                float wetL = 0f;
                float wetR = 0f;
                for (int c = 0; c < combL.Length; c++) {
                    wetL += combL[c].Process(input);
                    wetR += combR[c].Process(input);
                }

                for (int a = 0; a < allpassL.Length; a++) {
                    wetL = allpassL[a].Process(wetL);
                    wetR = allpassR[a].Process(wetR);
                }

                block[i] = left + wetL * wet1 + wetR * wet2;
                block[i + 1] = right + wetR * wet1 + wetL * wet2;
            }
        }

        static int ScaledLength(int referenceLength, double scale) {
            int scaled = (int)Math.Round(referenceLength * scale);
            return scaled < 1 ? 1 : scaled;
        }
    }
}

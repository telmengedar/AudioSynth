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
    /// structural, float-exact passthrough. <see cref="Process"/> is a send-return: it computes wet from a
    /// caller-supplied <c>send</c> span and adds it to a separate <c>master</c> span, never touching the dry
    /// signal already carried there. A caller that wants the original master-insert behaviour passes the same
    /// span as both <c>send</c> and <c>master</c> (<c>Process(block, block)</c>) — arithmetically identical to
    /// treating every voice as sending fully, since <c>master[i] += wetL</c> where wet was computed from
    /// <c>master</c> itself.
    /// </summary>
    public sealed class Reverb : IAudioEffect {

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
        /// Computes wet from <paramref name="send"/> and adds it to <paramref name="master"/> in place:
        /// each <c>master</c> frame becomes <c>[L + wetL, R + wetR]</c>; dry is never added since
        /// <paramref name="master"/> already carries it. <paramref name="send"/> may alias
        /// <paramref name="master"/> (the master-insert special case — see class summary); each frame's
        /// <c>send</c> values are read into locals before any write to that frame's <c>master</c>, so the
        /// aliased case is read-before-write safe. Allocation-free; the comb/allpass delay lines carry
        /// state across calls, so the decaying tail spans block boundaries.
        /// </summary>
        /// <param name="send">interleaved stereo send samples that feed the reverb; length must equal <paramref name="master"/>'s and be a multiple of 2</param>
        /// <param name="master">interleaved stereo master samples that the wet signal is added into</param>
        public void Process(ReadOnlySpan<float> send, Span<float> master) {
            if (master.Length % 2 != 0)
                throw new ArgumentException($"master length ({master.Length}) must be a multiple of 2 (interleaved stereo).", nameof(master));
            if (send.Length != master.Length)
                throw new ArgumentException($"send length ({send.Length}) must equal master length ({master.Length}).", nameof(send));

            for (int i = 0; i < master.Length; i += 2) {
                float sendL = send[i];
                float sendR = send[i + 1];
                float input = (sendL + sendR) * InputGain;

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

                master[i] += wetL * wet1 + wetR * wet2;
                master[i + 1] += wetR * wet1 + wetL * wet2;
            }
        }

        static int ScaledLength(int referenceLength, double scale) {
            int scaled = (int)Math.Round(referenceLength * scale);
            return scaled < 1 ? 1 : scaled;
        }
    }
}

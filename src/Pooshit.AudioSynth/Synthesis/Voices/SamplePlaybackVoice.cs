using System;

namespace Pooshit.AudioSynth.Synthesis.Voices {

    /// <summary>
    /// <see cref="IVoice"/> that reads a mono <see cref="SampleRegion"/> at a pitch-derived increment
    /// with linear interpolation and renders a mono block.  Each frame is scaled by the product of the
    /// region's DAHDSR <see cref="AmplitudeEnvelope"/> (the note's amplitude contour, which owns the
    /// click-free onset and the note-off release fade) and a <see cref="GainRamp"/> (the zipper-free
    /// slew of the velocity-derived scalar gain).  Supports no-loop one-shot and continuous looping.
    /// </summary>
    public sealed class SamplePlaybackVoice : IVoice {

        readonly SampleRegion region;
        readonly float pitchIncrement;
        GainRamp gainRamp;
        AmplitudeEnvelope envelope;
        double readPos;
        bool isActive;
        bool released;
        bool sampleExhausted;

        /// <summary>
        /// Creates a <see cref="SamplePlaybackVoice"/>.
        /// </summary>
        /// <param name="region">the sample region to play</param>
        /// <param name="pitchIncrement">fractional read-position advance per output frame</param>
        /// <param name="targetGain">velocity-derived gain target the ramp converges toward from zero</param>
        /// <param name="outputSampleRate">engine output sample rate; determines the gain-ramp slew speed</param>
        public SamplePlaybackVoice(SampleRegion region, float pitchIncrement, float targetGain, int outputSampleRate) {
            this.region = region ?? throw new ArgumentNullException(nameof(region));
            this.pitchIncrement = pitchIncrement;
            gainRamp = new GainRamp(outputSampleRate);
            gainRamp.SetTarget(targetGain);
            envelope = new AmplitudeEnvelope(region.Envelope, outputSampleRate);
            readPos = region.Start;
            isActive = true;
            released = false;
            sampleExhausted = false;
        }

        /// <inheritdoc/>
        public bool IsActive => isActive;

        /// <inheritdoc/>
        public void Release() {
            released = true;
            envelope.Release();
        }

        /// <inheritdoc/>
        public int RenderBlock(Span<float> block) {
            if (!isActive) {
                block.Clear();
                return block.Length;
            }

            int count = block.Length;
            for (int i = 0; i < count; i++) {
                float gain = envelope.AdvanceFrame() * gainRamp.AdvanceFrame();

                float sample;
                if (sampleExhausted) {
                    sample = 0f;
                } else {
                    sample = ReadInterpolated();
                    AdvanceReadPosition();
                }

                block[i] = sample * gain;

                if (released && envelope.IsFinished) {
                    for (int j = i + 1; j < count; j++)
                        block[j] = 0f;
                    isActive = false;
                    return count;
                }
            }

            if (sampleExhausted && !released)
                isActive = false;

            return count;
        }

        float ReadInterpolated() {
            float[] buf = region.Buffer;
            int regionEnd = region.End;

            int n = (int)readPos;
            float frac = (float)(readPos - n);

            float s0 = (n >= region.Start && n < regionEnd) ? buf[n] : 0f;
            int n1 = n + 1;
            float s1 = (n1 >= region.Start && n1 < regionEnd) ? buf[n1] : 0f;

            return s0 + frac * (s1 - s0);
        }

        void AdvanceReadPosition() {
            readPos += pitchIncrement;

            if (region.LoopMode == LoopMode.Continuous) {
                double loopLen = region.LoopEnd - region.LoopStart;
                if (readPos >= region.LoopEnd) {
                    double excess = readPos - region.LoopStart;
                    readPos = region.LoopStart + (excess % loopLen);
                }
            } else {
                if (readPos >= region.End)
                    sampleExhausted = true;
            }
        }
    }
}

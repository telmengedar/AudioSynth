using System;

namespace Pooshit.AudioSynth.Synthesis.Voices {

    /// <summary>
    /// <see cref="IVoice"/> that reads a mono <see cref="SampleRegion"/> at a pitch-derived increment
    /// with linear interpolation, multiplies each frame by its own <see cref="GainRamp"/>, and renders
    /// a mono block; supports no-loop one-shot and continuous looping.
    /// </summary>
    public sealed class SamplePlaybackVoice : IVoice {

        readonly SampleRegion _region;
        readonly float _pitchIncrement;
        GainRamp _gainRamp;
        double _readPos;
        bool _isActive;
        bool _released;
        bool _sampleExhausted;

        /// <summary>
        /// Creates a <see cref="SamplePlaybackVoice"/>.
        /// </summary>
        /// <param name="region">the sample region to play</param>
        /// <param name="pitchIncrement">fractional read-position advance per output frame</param>
        /// <param name="targetGain">velocity-derived gain target the ramp converges toward from zero</param>
        /// <param name="outputSampleRate">engine output sample rate; determines the gain-ramp slew speed</param>
        public SamplePlaybackVoice(SampleRegion region, float pitchIncrement, float targetGain, int outputSampleRate) {
            _region = region ?? throw new ArgumentNullException(nameof(region));
            _pitchIncrement = pitchIncrement;
            _gainRamp = new GainRamp(outputSampleRate);
            _gainRamp.SetTarget(targetGain);
            _readPos = region.Start;
            _isActive = true;
            _released = false;
            _sampleExhausted = false;
        }

        /// <inheritdoc/>
        public bool IsActive => _isActive;

        /// <inheritdoc/>
        public void Release() {
            _released = true;
            _gainRamp.SetTarget(0f);
        }

        /// <inheritdoc/>
        public int RenderBlock(Span<float> block) {
            if (!_isActive) {
                block.Clear();
                return block.Length;
            }

            int count = block.Length;
            for (int i = 0; i < count; i++) {
                float gain = _gainRamp.AdvanceFrame();

                float sample;
                if (_sampleExhausted) {
                    sample = 0f;
                } else {
                    sample = ReadInterpolated();
                    AdvanceReadPosition();
                }

                block[i] = sample * gain;

                if (_released && gain == 0f) {
                    for (int j = i + 1; j < count; j++)
                        block[j] = 0f;
                    _isActive = false;
                    return count;
                }
            }

            if (_sampleExhausted && !_released)
                _isActive = false;

            return count;
        }

        float ReadInterpolated() {
            float[] buf = _region.Buffer;
            int regionEnd = _region.End;

            int n = (int)_readPos;
            float frac = (float)(_readPos - n);

            float s0 = (n >= _region.Start && n < regionEnd) ? buf[n] : 0f;
            int n1 = n + 1;
            float s1 = (n1 >= _region.Start && n1 < regionEnd) ? buf[n1] : 0f;

            return s0 + frac * (s1 - s0);
        }

        void AdvanceReadPosition() {
            _readPos += _pitchIncrement;

            if (_region.LoopMode == LoopMode.Continuous) {
                double loopLen = _region.LoopEnd - _region.LoopStart;
                if (_readPos >= _region.LoopEnd) {
                    double excess = _readPos - _region.LoopStart;
                    _readPos = _region.LoopStart + (excess % loopLen);
                }
            } else {
                if (_readPos >= _region.End)
                    _sampleExhausted = true;
            }
        }
    }
}

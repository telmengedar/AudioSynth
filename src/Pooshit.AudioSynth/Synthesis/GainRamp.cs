using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Per-frame gain smoother that advances one frame at a time toward a target using a slew-rate limit
    /// derived from a smoothing time and the sample rate; block size is never an input, which makes
    /// zipper-noise at block boundaries structurally impossible (INV-1).
    /// </summary>
    public struct GainRamp {

        /// <summary>
        /// Default smoothing time in seconds; yields a perceptually instant but click-free glide.
        /// </summary>
        public const float DefaultSmoothingSeconds = 0.005f;

        float _current;
        float _target;
        readonly float _maxStepPerFrame;

        /// <summary>
        /// Creates a <see cref="GainRamp"/> starting at zero, targeting zero.
        /// </summary>
        /// <param name="sampleRate">output sample rate; determines the per-frame slew step</param>
        /// <param name="smoothingSeconds">time in seconds for a full-scale (0→1) ramp; defaults to 5 ms</param>
        public GainRamp(int sampleRate, float smoothingSeconds = DefaultSmoothingSeconds) {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (smoothingSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(smoothingSeconds));
            _current = 0f;
            _target = 0f;
            _maxStepPerFrame = 1f / (smoothingSeconds * sampleRate);
        }

        /// <summary>
        /// Current gain value; equals the value returned by the most recent <see cref="AdvanceFrame"/> call.
        /// </summary>
        public float Current => _current;

        /// <summary>
        /// True when the gain has converged to its target.
        /// </summary>
        public bool IsAtTarget => _current == _target;

        /// <summary>
        /// Sets a new target gain; the ramp continues from the current value without jumping.
        /// </summary>
        /// <param name="target">desired gain; typically in the range [0, 1]</param>
        public void SetTarget(float target) {
            _target = target;
        }

        /// <summary>
        /// Advances the gain by one frame toward the target and returns the gain for that frame.
        /// </summary>
        public float AdvanceFrame() {
            float diff = _target - _current;
            if (diff > _maxStepPerFrame)
                _current += _maxStepPerFrame;
            else if (diff < -_maxStepPerFrame)
                _current -= _maxStepPerFrame;
            else
                _current = _target;
            return _current;
        }
    }
}

using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable construction-time parameter surface for <see cref="Chorus"/>: LFO rate, modulation
    /// depth, centre delay, wet mix and voice count, each clamped to a sensible, stable range.
    /// <see cref="BaseDelayMs"/> is clamped to always exceed <see cref="DepthMs"/> by at least
    /// <see cref="MinDelayMarginMs"/>, so the modulated read position <c>baseDelay + depth·sin(lfo)</c>
    /// can never reach zero or go negative, regardless of caller input. <see cref="Wet"/> = 0 is a
    /// structural dry passthrough: <see cref="Chorus"/> adds nothing to master when wet is zero.
    /// </summary>
    public sealed class ChorusSettings {

        /// <summary>Default LFO rate, in Hz.</summary>
        public const float DefaultRateHz = 0.8f;

        /// <summary>Default modulation depth, in milliseconds.</summary>
        public const float DefaultDepthMs = 3f;

        /// <summary>Default centre (unmodulated) delay, in milliseconds.</summary>
        public const float DefaultBaseDelayMs = 20f;

        /// <summary>Default wet mix.</summary>
        public const float DefaultWet = 0.5f;

        /// <summary>Default chorus voice count.</summary>
        public const int DefaultVoiceCount = 3;

        const float MinRateHz = 0f;
        const float MaxRateHz = 20f;
        const float MinDepthMs = 0f;
        const float MaxDepthMs = 50f;
        const float MinBaseDelayMs = 0.1f;
        const float MaxBaseDelayMs = 100f;
        const int MinVoiceCount = 1;
        const int MaxVoiceCount = 4;

        /// <summary>
        /// Minimum margin, in milliseconds, by which <see cref="BaseDelayMs"/> must exceed
        /// <see cref="DepthMs"/> so the modulated read position never reaches zero.
        /// </summary>
        const float MinDelayMarginMs = 0.1f;

        /// <summary>
        /// A conventional chorus preset (<see cref="DefaultRateHz"/>, <see cref="DefaultDepthMs"/>,
        /// <see cref="DefaultBaseDelayMs"/>, <see cref="DefaultWet"/>, <see cref="DefaultVoiceCount"/>),
        /// suitable wherever a caller wants thickening/width without hand-tuning.
        /// </summary>
        public static readonly ChorusSettings Default =
            new ChorusSettings(DefaultRateHz, DefaultDepthMs, DefaultBaseDelayMs, DefaultWet, DefaultVoiceCount);

        /// <summary>
        /// Creates <see cref="ChorusSettings"/>, clamping every parameter to a stable range and
        /// guaranteeing <see cref="BaseDelayMs"/> exceeds <see cref="DepthMs"/>.
        /// </summary>
        /// <param name="rateHz">LFO rate in Hz; clamped to [0, 20]</param>
        /// <param name="depthMs">modulation depth in milliseconds; clamped to [0, 50]</param>
        /// <param name="baseDelayMs">centre delay in milliseconds; clamped to [0.1, 100], then raised above the clamped <paramref name="depthMs"/> if needed</param>
        /// <param name="wet">wet mix in [0, 1]; 0 is a structural dry passthrough</param>
        /// <param name="voiceCount">chorus voice count; clamped to [1, 4]</param>
        public ChorusSettings(
            float rateHz = DefaultRateHz,
            float depthMs = DefaultDepthMs,
            float baseDelayMs = DefaultBaseDelayMs,
            float wet = DefaultWet,
            int voiceCount = DefaultVoiceCount) {
            RateHz = Clamp(rateHz, MinRateHz, MaxRateHz);
            Wet = Clamp(wet, 0f, 1f);
            VoiceCount = Clamp(voiceCount, MinVoiceCount, MaxVoiceCount);

            float clampedDepth = Clamp(depthMs, MinDepthMs, MaxDepthMs);
            float clampedBaseDelay = Clamp(baseDelayMs, MinBaseDelayMs, MaxBaseDelayMs);
            if (clampedBaseDelay <= clampedDepth)
                clampedBaseDelay = clampedDepth + MinDelayMarginMs;

            DepthMs = clampedDepth;
            BaseDelayMs = clampedBaseDelay;
        }

        /// <summary>LFO rate in Hz, in [0, 20].</summary>
        public float RateHz { get; }

        /// <summary>Modulation depth in milliseconds, in [0, 50].</summary>
        public float DepthMs { get; }

        /// <summary>Centre (unmodulated) delay in milliseconds; always strictly greater than <see cref="DepthMs"/>.</summary>
        public float BaseDelayMs { get; }

        /// <summary>Wet mix in [0, 1]; 0 is a structural dry passthrough.</summary>
        public float Wet { get; }

        /// <summary>Chorus voice count, in [1, 4].</summary>
        public int VoiceCount { get; }

        static float Clamp(float value, float min, float max) {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        static int Clamp(int value, int min, int max) {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}

using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable construction-time parameter surface for <see cref="Reverb"/>: room size, damping, wet
    /// mix and stereo width, each a normalized scalar clamped to [0, 1]. <see cref="RoomSize"/> maps to
    /// comb <see cref="Feedback"/> via <c>feedback = RoomSize·0.28 + 0.7</c>, which always lands in
    /// [0.7, 0.98] and therefore always strictly below 1 — a <see cref="Reverb"/> built from these
    /// settings can never be driven into instability by caller input, regardless of the value passed
    /// here. <see cref="Wet"/> = 0 is a structural dry passthrough: <see cref="Reverb"/> mixes
    /// <c>dry·1.0 + wet·0.0</c>, which is float-exact.
    /// </summary>
    public sealed class ReverbSettings {

        /// <summary>Slope mapping <see cref="RoomSize"/> onto comb <see cref="Feedback"/> (standard Freeverb tuning).</summary>
        const float FeedbackRoomScale = 0.28f;

        /// <summary>Offset mapping <see cref="RoomSize"/> onto comb <see cref="Feedback"/> (standard Freeverb tuning).</summary>
        const float FeedbackRoomOffset = 0.7f;

        /// <summary>Default room size: a mid-size hall.</summary>
        public const float DefaultRoomSize = 0.7f;

        /// <summary>Default in-feedback damping.</summary>
        public const float DefaultDamping = 0.5f;

        /// <summary>Default wet mix.</summary>
        public const float DefaultWet = 0.25f;

        /// <summary>Default stereo width.</summary>
        public const float DefaultWidth = 1.0f;

        /// <summary>
        /// A sensible mid-size hall preset (<see cref="DefaultRoomSize"/>, <see cref="DefaultDamping"/>,
        /// <see cref="DefaultWet"/>, <see cref="DefaultWidth"/>), suitable wherever a caller wants room
        /// ambience without hand-tuning.
        /// </summary>
        public static readonly ReverbSettings Default = new ReverbSettings(DefaultRoomSize, DefaultDamping, DefaultWet, DefaultWidth);

        /// <summary>
        /// Creates <see cref="ReverbSettings"/>, clamping every parameter to [0, 1] so <see cref="Feedback"/>
        /// can never reach or exceed 1.
        /// </summary>
        /// <param name="roomSize">room size in [0, 1]; maps to comb <see cref="Feedback"/>, always &lt; 1</param>
        /// <param name="damping">in-feedback damping in [0, 1]; higher darkens and shortens the tail</param>
        /// <param name="wet">wet mix in [0, 1]; 0 is a structural dry passthrough</param>
        /// <param name="width">stereo width in [0, 1]; 0 collapses L/R wet to mono, 1 is fully decorrelated</param>
        public ReverbSettings(float roomSize = DefaultRoomSize, float damping = DefaultDamping, float wet = DefaultWet, float width = DefaultWidth) {
            RoomSize = Clamp01(roomSize);
            Damping = Clamp01(damping);
            Wet = Clamp01(wet);
            Width = Clamp01(width);
            Feedback = RoomSize * FeedbackRoomScale + FeedbackRoomOffset;
        }

        /// <summary>Room size in [0, 1]; drives <see cref="Feedback"/>.</summary>
        public float RoomSize { get; }

        /// <summary>In-feedback one-pole damping in [0, 1].</summary>
        public float Damping { get; }

        /// <summary>Wet mix in [0, 1]; 0 is a structural dry passthrough.</summary>
        public float Wet { get; }

        /// <summary>Stereo width in [0, 1].</summary>
        public float Width { get; }

        /// <summary>
        /// Comb feedback derived from <see cref="RoomSize"/>; always in [0.7, 0.98], strictly below 1
        /// (BIBO-stable by construction).
        /// </summary>
        public float Feedback { get; }

        static float Clamp01(float value) {
            if (value < 0f)
                return 0f;
            if (value > 1f)
                return 1f;
            return value;
        }
    }
}

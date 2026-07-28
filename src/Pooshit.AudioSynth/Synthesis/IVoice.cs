using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// A single sounding note in flight; renders its own mono block that the engine mixes and pans.
    /// </summary>
    public interface IVoice {

        /// <summary>
        /// True while the voice is producing sound, including its release tail.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Renders one mono block and returns the sample count produced; zero once finished.
        /// </summary>
        int RenderBlock(Span<float> block);

        /// <summary>
        /// Enters the release phase on note-off.
        /// </summary>
        void Release();

        /// <summary>
        /// Sets the voice's pitch-bend ratio, a dimensionless multiplicative factor applied to the
        /// voice's read-position increment (1.0 = no bend).
        /// </summary>
        void SetPitchBend(float pitchFactor);

        /// <summary>
        /// Sets the voice's live mod-wheel vibrato depth, typically driven by MIDI CC1, in [0,1];
        /// 0 means no mod-wheel vibrato.
        /// </summary>
        void SetModWheel(float amount);

        /// <summary>
        /// The voice's static stereo position in [-1,1] (-1 = full left, 0 = centre, +1 = full right),
        /// immutable for the voice's lifetime; combined with the channel's dynamic pan at mix time.
        /// </summary>
        float Pan { get; }

        /// <summary>
        /// The voice's static reverb-send weight in [0,1], immutable for the voice's lifetime; combined
        /// multiplicatively with the channel's dynamic <see cref="ISynthesizer.SetChannelReverbSend"/>
        /// weight at mix time. <c>0</c> for inactive/silent voices.
        /// </summary>
        float ReverbSend { get; }

        /// <summary>
        /// The voice's static chorus-send weight in [0,1], immutable for the voice's lifetime; combined
        /// additively with the channel's dynamic <see cref="ISynthesizer.SetChannelChorusSend"/> weight
        /// at mix time (clamped to [0,1]). <c>0</c> for inactive/silent voices.
        /// </summary>
        float ChorusSend { get; }
    }
}

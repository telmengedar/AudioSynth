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
        /// additively with the channel's dynamic <see cref="ISynthesizer.SetChannelReverbSend"/>
        /// weight at mix time (clamped to [0,1]). <c>0</c> for inactive/silent voices.
        /// </summary>
        float ReverbSend { get; }

        /// <summary>
        /// The voice's static chorus-send weight in [0,1], immutable for the voice's lifetime; combined
        /// additively with the channel's dynamic <see cref="ISynthesizer.SetChannelChorusSend"/> weight
        /// at mix time (clamped to [0,1]). <c>0</c> for inactive/silent voices.
        /// </summary>
        float ChorusSend { get; }

        /// <summary>
        /// The amplitude the voice is currently producing (its last-rendered frame's envelope ×
        /// gain-ramp × tremolo product), in [0,1]; the engine reads this to pick the quietest sounding
        /// victim when the voice pool is full and a note must be stolen. <c>0</c> for inactive voices.
        /// Reading never mutates the voice.
        /// </summary>
        float CurrentGain { get; }

        /// <summary>
        /// Instructs the voice to ramp to silence over a short, click-free window (reusing its own
        /// gain smoothing) and then become inactive, regardless of its natural release time; called by
        /// the engine when this voice's slot is reclaimed for a new note. Idempotent, and a no-op on an
        /// already-inactive voice.
        /// </summary>
        void FastFadeForSteal();

        /// <summary>
        /// SF2 generator 57 (exclusiveClass) value carried from the voice's region, immutable for the
        /// voice's lifetime; <c>0</c> means the voice belongs to no choke group. Read only by the engine,
        /// which fast-fades every other sounding, same-channel voice sharing a non-zero class when this
        /// voice starts (SF2 spec choke, e.g. GM hi-hats).
        /// </summary>
        int ExclusiveClass { get; }
    }
}

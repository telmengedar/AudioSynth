using Pooshit.AudioSynth.Audio;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Top-level engine seam: a pull <see cref="IAudioSource"/> that turns MIDI-style note events into voices and renders their mix.
    /// </summary>
    public interface ISynthesizer : IAudioSource {

        /// <summary>
        /// Starts a note, allocating or stealing a voice.
        /// </summary>
        void NoteOn(int channel, int key, int velocity);

        /// <summary>
        /// Releases a sounding note into its envelope tail.
        /// </summary>
        void NoteOff(int channel, int key);

        /// <summary>
        /// Sets the current patch for a channel; only future <see cref="NoteOn"/> calls on it are affected.
        /// </summary>
        void SetChannelPatch(int channel, IPatch patch);

        /// <summary>
        /// Sets a channel's mix gain, glided rather than stepped; mirrors <see cref="SetChannelPatch"/>.
        /// </summary>
        void SetChannelGain(int channel, float gain);

        /// <summary>
        /// Sets a channel's pitch bend as a signed semitone offset (0 = centered), glided rather than
        /// stepped; applies to the channel's currently-sounding voices and is inherited by future
        /// <see cref="NoteOn"/> calls on the same channel. MIDI-neutral: mirrors <see cref="SetChannelGain"/>.
        /// </summary>
        void SetChannelPitchBend(int channel, float semitones);

        /// <summary>
        /// Sets a channel's mod-wheel vibrato depth (typically driven by MIDI CC1), a scalar expected
        /// in [0,1]; applies to the channel's currently-sounding voices and is inherited by future
        /// <see cref="NoteOn"/> calls on the same channel. MIDI-neutral: mirrors
        /// <see cref="SetChannelPitchBend"/>.
        /// </summary>
        void SetChannelModulation(int channel, float amount);

        /// <summary>
        /// Sets a channel's stereo pan position, a signed value where -1 = full left, 0 = centre, and
        /// +1 = full right; applies to the channel's currently-sounding and future voices, read live
        /// each block rather than captured at note-on. MIDI-neutral: mirrors <see cref="SetChannelGain"/>.
        /// </summary>
        void SetChannelPan(int channel, float pan);

        /// <summary>
        /// Sets a channel's reverb-send weight (typically driven by MIDI CC91), a scalar expected in
        /// [0,1]; applies to the channel's currently-sounding and future voices, read live each block
        /// rather than captured at note-on, and combined additively with each voice's static
        /// <see cref="IVoice.ReverbSend"/> (clamped to [0,1]). MIDI-neutral: mirrors <see cref="SetChannelPan"/>.
        /// </summary>
        void SetChannelReverbSend(int channel, float level);

        /// <summary>
        /// Sets a channel's chorus-send weight (typically driven by MIDI CC93), a scalar expected in
        /// [0,1]; applies to the channel's currently-sounding and future voices, read live each block
        /// rather than captured at note-on, and combined additively with each voice's static
        /// <see cref="IVoice.ChorusSend"/> (clamped to [0,1] — never multiplicatively, so a voice's
        /// absent gen-15 bias never nullifies the channel's send). MIDI-neutral: mirrors
        /// <see cref="SetChannelReverbSend"/>.
        /// </summary>
        void SetChannelChorusSend(int channel, float level);

        /// <summary>
        /// Sets a channel's sustain (hold) pedal state, typically driven by MIDI CC64. While held,
        /// a <see cref="NoteOff"/> on the channel defers the voice's release instead of releasing it
        /// immediately; when disengaged, every voice on the channel deferred since the pedal went down
        /// releases into its envelope tail. A note that never received a <see cref="NoteOff"/> is
        /// unaffected by disengaging the pedal. MIDI-neutral: mirrors <see cref="SetChannelPan"/>.
        /// </summary>
        void SetChannelSustain(int channel, bool held);

        /// <summary>
        /// Fast-fades every currently-sounding voice on the channel to silence over the standard
        /// click-free declick window (typically driven by MIDI CC120, All Sound Off), regardless of
        /// the channel's sustain-pedal state; no envelope release runs. Any note deferred behind a
        /// slot's own steal fade on the channel is cancelled so it cannot spring to life once the
        /// fade completes. A no-op on a channel with no occupied voices; other channels are untouched.
        /// MIDI-neutral: reuses the same declick path as voice-stealing.
        /// </summary>
        void SilenceChannel(int channel);

        /// <summary>
        /// Releases every currently-sounding voice on the channel exactly as if a <see cref="NoteOff"/>
        /// had arrived for its key (typically driven by MIDI CC123, All Notes Off): deferred to the
        /// channel's pending-release state while its sustain pedal is held, else released into its
        /// normal envelope tail. Idempotent on already-released voices. A no-op on a channel with no
        /// occupied voices; other channels are untouched. MIDI-neutral: mirrors <see cref="NoteOff"/>.
        /// </summary>
        void ReleaseAllNotes(int channel);
    }
}

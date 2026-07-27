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
    }
}

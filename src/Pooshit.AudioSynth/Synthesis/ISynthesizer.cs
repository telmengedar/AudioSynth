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
    }
}

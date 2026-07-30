using System.Collections.Generic;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Optional extension of <see cref="IPatch"/> for patches that can start more than one voice for a
    /// single note-on (SF2 zone/layer stacking). A separate interface rather than a default interface
    /// method, since this library also targets <c>netstandard2.0</c>, which doesn't support those.
    /// Patches that don't implement it fall back to the single-voice <see cref="IPatch.StartVoice"/>.
    /// </summary>
    public interface IMultiVoicePatch : IPatch {

        /// <summary>
        /// Starts every voice needed for one note-on (0..N layers, deterministic resolver order) and
        /// appends them to <paramref name="voices"/>. Does not clear <paramref name="voices"/> — the
        /// caller owns and clears the buffer, so the engine can reuse it across note-ons without
        /// allocating a fresh list per note.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <param name="voices">caller-owned, caller-cleared buffer that layers are appended to</param>
        void StartVoices(int key, int velocity, List<IVoice> voices);
    }
}

using System.Collections.Generic;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Optional extension of <see cref="IPatch"/> for archetypes that can start more than one voice for a
    /// single note-on — SF2 zone/layer stacking (DiVoid #7282): many SF2 presets are authored as stacks of
    /// overlapping zones (e.g. a heavily-attenuated sustained tonal zone plus a near-0-cB attack/click
    /// companion) that a compliant player sounds simultaneously.
    /// </summary>
    /// <remarks>
    /// This is a separate interface rather than a default-implemented member on <see cref="IPatch"/>
    /// because this library also targets <c>netstandard2.0</c>, whose runtime does not support default
    /// interface method implementations (they fail to build there, unlike on <c>net8.0</c>). A marker
    /// interface keeps the same "additive, minimal blast radius" property the default-method design would
    /// have had: patches that only ever produce one voice per note — <c>SamplePatch</c> and every
    /// test/demo <c>IPatch</c> helper — simply do not implement <see cref="IMultiVoicePatch"/> and are
    /// completely untouched; the engine falls back to the single-voice <see cref="IPatch.StartVoice"/>
    /// for them. <see cref="Formats.Sf2.Sf2Patch"/> is currently the only implementer.
    /// </remarks>
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

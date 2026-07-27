using System.IO;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats {

    /// <summary>
    /// Sound-bank loader seam. v1 ships an SF2 implementation; SFZ and the native .bank format plug in here later behind the same untrusted-input contract.
    /// </summary>
    public interface ISoundBankLoader {

        /// <summary>
        /// Short identifier of the format handled, e.g. "sf2".
        /// </summary>
        string FormatId { get; }

        /// <summary>
        /// Loads a sound bank from a bank stream, validating all sizes and offsets as untrusted input and throwing a typed error on malformed data.
        /// </summary>
        SoundBank Load(Stream source);
    }
}

using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="IPatch"/> that always starts a <see cref="NanEmittingVoice"/>; used to
    /// verify the synthesizer's NaN/Inf-safe finalize choke point (INV-2).
    /// </summary>
    internal sealed class NanEmittingPatch : IPatch {

        /// <inheritdoc/>
        public IVoice StartVoice(int key, int velocity) => new NanEmittingVoice();
    }
}

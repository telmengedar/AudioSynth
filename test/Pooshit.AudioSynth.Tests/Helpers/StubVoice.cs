using System;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="IVoice"/> that is immediately inactive and renders silence; used by
    /// <see cref="StubPatch"/> where only patch identity, not audio, matters.
    /// </summary>
    internal sealed class StubVoice : IVoice {

        /// <inheritdoc/>
        public bool IsActive => false;

        /// <inheritdoc/>
        public void Release() { }

        /// <inheritdoc/>
        public int RenderBlock(Span<float> block) {
            block.Clear();
            return block.Length;
        }
    }
}

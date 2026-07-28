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
        public void SetPitchBend(float pitchFactor) { }

        /// <inheritdoc/>
        public void SetModWheel(float amount) { }

        /// <inheritdoc/>
        public float Pan => 0f;

        /// <inheritdoc/>
        public float ReverbSend => 0f;

        /// <inheritdoc/>
        public float ChorusSend => 0f;

        /// <inheritdoc/>
        public float CurrentGain => 0f;

        /// <inheritdoc/>
        public void FastFadeForSteal() { }

        /// <inheritdoc/>
        public int RenderBlock(Span<float> block) {
            block.Clear();
            return block.Length;
        }
    }
}

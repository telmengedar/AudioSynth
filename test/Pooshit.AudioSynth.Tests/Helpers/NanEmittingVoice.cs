using System;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="IVoice"/> that writes a pattern of NaN and Inf values into every block
    /// to exercise the synthesizer's finalize choke point (INV-2).
    /// </summary>
    internal sealed class NanEmittingVoice : IVoice {

        int _blocksRendered;

        /// <inheritdoc/>
        public bool IsActive => _blocksRendered < 4;

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
        public int RenderBlock(Span<float> block) {
            for (int i = 0; i < block.Length; i++) {
                switch (i % 3) {
                    case 0: block[i] = float.NaN; break;
                    case 1: block[i] = float.PositiveInfinity; break;
                    default: block[i] = float.NegativeInfinity; break;
                }
            }
            _blocksRendered++;
            return block.Length;
        }
    }
}

using System;

namespace Pooshit.AudioSynth.Synthesis.Voices {

    /// <summary>
    /// An <see cref="IVoice"/> that is immediately inactive: <see cref="IsActive"/> is false,
    /// <see cref="RenderBlock"/> emits silence, and <see cref="Release"/> is a no-op.
    /// Returned by patches when no zone matches a note so that the engine can reclaim the slot
    /// on the next block without branching on null.
    /// </summary>
    internal sealed class InactiveVoice : IVoice {

        /// <summary>Shared singleton; safe to return multiple times.</summary>
        internal static readonly InactiveVoice Instance = new InactiveVoice();

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
        public int RenderBlock(Span<float> block) {
            block.Clear();
            return block.Length;
        }
    }
}

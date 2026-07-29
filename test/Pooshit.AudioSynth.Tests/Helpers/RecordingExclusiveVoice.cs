using System;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only active <see cref="IVoice"/> that reports a settable <see cref="ExclusiveClass"/> and
    /// records whether <see cref="FastFadeForSteal"/> was called, so exclusive-class choke tests
    /// (DiVoid #7226/#7227) can observe the engine's choke decision directly instead of inferring it
    /// from rendered audio levels. Unlike <see cref="StubVoice"/> (immediately inactive), this voice
    /// stays active until choked, so it occupies its pool slot the way a real sounding voice would.
    /// </summary>
    internal sealed class RecordingExclusiveVoice : IVoice {

        /// <summary>
        /// Creates a <see cref="RecordingExclusiveVoice"/> reporting <paramref name="exclusiveClass"/>.
        /// </summary>
        internal RecordingExclusiveVoice(int exclusiveClass) {
            ExclusiveClass = exclusiveClass;
        }

        /// <summary>
        /// True once the engine has called <see cref="FastFadeForSteal"/> on this voice.
        /// </summary>
        internal bool FastFadeForStealCalled { get; private set; }

        /// <inheritdoc/>
        public bool IsActive => true;

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
        public int ExclusiveClass { get; }

        /// <inheritdoc/>
        public float CurrentGain => 1f;

        /// <inheritdoc/>
        public void FastFadeForSteal() {
            FastFadeForStealCalled = true;
        }

        /// <inheritdoc/>
        public int RenderBlock(Span<float> block) {
            block.Clear();
            return block.Length;
        }
    }
}

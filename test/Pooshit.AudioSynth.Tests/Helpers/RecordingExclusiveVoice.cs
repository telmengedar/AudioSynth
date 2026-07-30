using System;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only active <see cref="IVoice"/> that reports a settable <see cref="ExclusiveClass"/> and
    /// records whether <see cref="FastFadeForSteal"/> was called, so choke tests can observe the engine's
    /// decision directly instead of inferring it from rendered audio levels. Unlike <see cref="StubVoice"/>
    /// (immediately inactive), this voice stays active until choked.
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

        /// <summary>
        /// True once the engine has called <see cref="Release"/> on this voice (e.g. via
        /// <see cref="ISynthesizer.NoteOff"/> or <see cref="ISynthesizer.ReleaseAllNotes"/>).
        /// </summary>
        internal bool ReleaseCalled { get; private set; }

        /// <summary>
        /// Number of times <see cref="RenderBlock"/> has been called on this voice, so a test can assert
        /// a layer parked behind a steal (<see cref="Synthesis.VoiceSlot.PendingVoice"/>) that gets
        /// cancelled (e.g. by <see cref="ISynthesizer.SilenceChannel"/>) never actually starts rendering.
        /// </summary>
        internal int RenderBlockCallCount { get; private set; }

        /// <inheritdoc/>
        public bool IsActive => true;

        /// <inheritdoc/>
        public void Release() {
            ReleaseCalled = true;
        }

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
            RenderBlockCallCount++;
            block.Clear();
            return block.Length;
        }
    }
}

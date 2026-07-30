using System.Collections.Generic;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="IMultiVoicePatch"/> that starts a fixed number of <see cref="RecordingExclusiveVoice"/>
    /// layers per note-on, recording every started voice so SF2 zone/layer stacking engine tests (DiVoid
    /// #7282) can assert on slot placement, release, and choke behaviour without needing a real SF2 preset.
    /// </summary>
    internal sealed class MultiVoicePatch : IMultiVoicePatch {

        readonly int voiceCount;
        readonly int exclusiveClass;

        /// <summary>
        /// Creates a <see cref="MultiVoicePatch"/> that starts <paramref name="voiceCount"/> voices per
        /// note-on, each reporting <paramref name="exclusiveClass"/> (default 0, no choke group).
        /// </summary>
        internal MultiVoicePatch(int voiceCount, int exclusiveClass = 0) {
            this.voiceCount = voiceCount;
            this.exclusiveClass = exclusiveClass;
        }

        /// <summary>
        /// Every voice started across every <see cref="StartVoice"/>/<see cref="StartVoices"/> call, in
        /// call order.
        /// </summary>
        internal List<RecordingExclusiveVoice> StartedVoices { get; } = new List<RecordingExclusiveVoice>();

        /// <inheritdoc/>
        public IVoice StartVoice(int key, int velocity) {
            RecordingExclusiveVoice voice = new RecordingExclusiveVoice(exclusiveClass);
            StartedVoices.Add(voice);
            return voice;
        }

        /// <inheritdoc/>
        public void StartVoices(int key, int velocity, List<IVoice> voices) {
            for (int i = 0; i < voiceCount; i++) {
                RecordingExclusiveVoice voice = new RecordingExclusiveVoice(exclusiveClass);
                StartedVoices.Add(voice);
                voices.Add(voice);
            }
        }
    }
}

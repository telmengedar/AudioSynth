using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="IPatch"/> that starts <see cref="RecordingExclusiveVoice"/> instances
    /// reporting a fixed exclusive class, recording the most recently started voice so exclusive-class
    /// choke tests (DiVoid #7226/#7227) can assert on it after the triggering <see cref="ISynthesizer.NoteOn"/>.
    /// </summary>
    internal sealed class ExclusiveClassPatch : IPatch {

        readonly int exclusiveClass;

        /// <summary>
        /// Creates an <see cref="ExclusiveClassPatch"/> whose started voices report <paramref name="exclusiveClass"/>.
        /// </summary>
        internal ExclusiveClassPatch(int exclusiveClass) {
            this.exclusiveClass = exclusiveClass;
        }

        /// <summary>
        /// The most recently started voice, or <c>null</c> before the first <see cref="StartVoice"/> call.
        /// </summary>
        internal RecordingExclusiveVoice? LastVoice { get; private set; }

        /// <inheritdoc/>
        public IVoice StartVoice(int key, int velocity) {
            RecordingExclusiveVoice voice = new RecordingExclusiveVoice(exclusiveClass);
            LastVoice = voice;
            return voice;
        }
    }
}

using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Identity-distinguishable no-op <see cref="IPatch"/> test double; carries a name so fallback
    /// and per-channel routing tests can assert which patch instance was selected.
    /// </summary>
    internal sealed class StubPatch : IPatch {

        internal StubPatch(string name) {
            Name = name;
        }

        /// <summary>
        /// Name assigned at construction, used to identify this patch in assertions.
        /// </summary>
        internal string Name { get; }

        /// <inheritdoc/>
        public IVoice StartVoice(int key, int velocity) => new StubVoice();

        /// <inheritdoc/>
        public override string ToString() => Name;
    }
}

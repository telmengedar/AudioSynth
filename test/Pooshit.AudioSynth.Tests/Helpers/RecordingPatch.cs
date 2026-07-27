using System.Collections.Generic;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Test-only <see cref="IPatch"/> that records every <see cref="StartVoice"/> call so tests can
    /// assert which patch instance a channel actually routed a note to.
    /// </summary>
    internal sealed class RecordingPatch : IPatch {

        /// <summary>
        /// Every (key, velocity) pair passed to <see cref="StartVoice"/>, in call order.
        /// </summary>
        internal List<(int Key, int Velocity)> StartVoiceCalls { get; } = new List<(int, int)>();

        /// <inheritdoc/>
        public IVoice StartVoice(int key, int velocity) {
            StartVoiceCalls.Add((key, velocity));
            return new StubVoice();
        }
    }
}

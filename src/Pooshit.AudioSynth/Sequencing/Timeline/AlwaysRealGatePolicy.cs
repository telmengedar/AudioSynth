namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// Default <see cref="IGatePolicy"/>: always resolves <see cref="GateDecision.Real"/>, so BGM playback
    /// and the editor are unaffected by gating.
    /// </summary>
    public sealed class AlwaysRealGatePolicy : IGatePolicy {

        /// <inheritdoc/>
        public GateDecision Decide(GateGroup group, bool triggered) => GateDecision.Real;
    }
}

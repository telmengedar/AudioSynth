namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// Injected rhythm-game decision seam (Phase 3): pure decision, no dispatch. Not consulted by the
    /// Phase 1 driver, which never constructs a <see cref="GateGroup"/>.
    /// </summary>
    public interface IGatePolicy {

        /// <summary>Decides how <paramref name="group"/> resolves given whether it was triggered in-window.</summary>
        GateDecision Decide(GateGroup group, bool triggered);
    }
}

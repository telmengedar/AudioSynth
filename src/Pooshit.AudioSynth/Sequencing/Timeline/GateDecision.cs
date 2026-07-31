namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// Outcome of an <see cref="IGatePolicy"/> decision for a <see cref="GateGroup"/> (Phase 3 seam);
    /// not consulted by the Phase 1 driver, which never sees a gated entry.
    /// </summary>
    public enum GateDecision {

        /// <summary>Dispatch the group's real payload entries.</summary>
        Real,

        /// <summary>Dispatch the group's substitute payload entries.</summary>
        Substitute,

        /// <summary>Dispatch neither payload.</summary>
        Silent
    }
}

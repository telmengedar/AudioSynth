namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Instrument archetype seam: a patch describes a sound and starts a runtime <see cref="IVoice"/> for a played note. Concrete archetypes (basic, FM, multi, SF2, SFZ) supply the bodies.
    /// </summary>
    public interface IPatch {

        /// <summary>
        /// Starts a live voice for the given note and velocity.
        /// </summary>
        IVoice StartVoice(int key, int velocity);
    }
}

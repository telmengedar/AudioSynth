namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Emission target for the cell decisions made by <see cref="TrackerCellApplier"/>: the same five verbs
    /// land either as timeline events (offline) or as live synth calls.
    /// </summary>
    public interface ITrackerCellSink {

        /// <summary>Sets a channel's mix gain.</summary>
        /// <param name="channel">target channel</param>
        /// <param name="gain">gain in [0,1]</param>
        void SetGain(int channel, float gain);

        /// <summary>Selects a channel's patch by symbolic bank/program; affects future notes only.</summary>
        /// <param name="channel">target channel</param>
        /// <param name="bank">SoundBank bank number</param>
        /// <param name="program">SoundBank program number</param>
        void SelectPatch(int channel, int bank, int program);

        /// <summary>Starts a note on a channel.</summary>
        /// <param name="channel">target channel</param>
        /// <param name="key">MIDI key</param>
        /// <param name="velocity">note velocity</param>
        void NoteOn(int channel, int key, int velocity);

        /// <summary>Releases a channel's sounding note into its envelope tail.</summary>
        /// <param name="channel">target channel</param>
        /// <param name="key">MIDI key of the sounding note</param>
        void NoteOff(int channel, int key);

        /// <summary>Silences a channel immediately, without an envelope release.</summary>
        /// <param name="channel">target channel</param>
        void Silence(int channel);
    }
}

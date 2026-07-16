namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Determines how a <see cref="SampleRegion"/> loops during playback.
    /// </summary>
    public enum LoopMode {

        /// <summary>
        /// Play the sample once from start to end; the voice goes silent after the last sample.
        /// </summary>
        NoLoop = 0,

        /// <summary>
        /// Loop the sample continuously between <c>loopStart</c> and <c>loopEnd</c> until the voice is released.
        /// </summary>
        Continuous = 1
    }
}

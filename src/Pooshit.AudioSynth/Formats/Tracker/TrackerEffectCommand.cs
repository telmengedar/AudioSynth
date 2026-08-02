namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// Library-known effect commands carried by a <see cref="Cell"/>'s effect sub-column. Backed by a
    /// <c>byte</c> so the whole vocabulary is open: values without a named member are legal and pass
    /// through the importer uninterpreted, letting a game engine define custom commands without breaking
    /// the format.
    /// </summary>
    public enum TrackerEffectCommand : byte {

        /// <summary>No effect (the default, empty sub-column).</summary>
        None = 0,

        /// <summary>Set the playback speed in ticks per row; parameter is the tick count.</summary>
        SetSpeed = 1,

        /// <summary>Set the playback tempo in BPM; parameter is the beats-per-minute value.</summary>
        SetTempo = 2
    }
}

namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// Sentinel constants and pure key mapping helpers for a <see cref="Cell"/>'s note sub-column. Kept
    /// off <see cref="Cell"/> itself so the cell stays a pure, behavior-free value type for serialization.
    /// The note byte encodes: <c>0</c> empty, <c>1..120</c> a playable note (MIDI key = value − 1),
    /// <c>254</c> note-off (release), <c>255</c> note-cut (immediate silence).
    /// </summary>
    public static class TrackerNotes {

        /// <summary>Empty note sub-column: no note event.</summary>
        public const byte Empty = 0;

        /// <summary>Lowest playable note value (MIDI key 0).</summary>
        public const byte LowestPlayable = 1;

        /// <summary>Highest playable note value (MIDI key 119).</summary>
        public const byte HighestPlayable = 120;

        /// <summary>Note-off: releases the channel's sounding note into its envelope tail.</summary>
        public const byte Off = 254;

        /// <summary>Note-cut: silences the channel's sounding note immediately, without an envelope release.</summary>
        public const byte Cut = 255;

        /// <summary>
        /// Tests whether a note byte holds a playable note (as opposed to empty / off / cut).
        /// </summary>
        /// <param name="note">the note sub-column value</param>
        /// <returns>true if the value maps to a playable MIDI key</returns>
        public static bool IsPlayable(byte note) => note >= LowestPlayable && note <= HighestPlayable;

        /// <summary>
        /// Maps a playable note byte to its MIDI key; only meaningful when <see cref="IsPlayable"/> is true.
        /// </summary>
        /// <param name="note">a playable note sub-column value</param>
        /// <returns>the MIDI key number</returns>
        public static int KeyOf(byte note) => note - 1;

        /// <summary>
        /// Maps a MIDI key to its playable note byte, the inverse of <see cref="KeyOf"/>, for authoring.
        /// </summary>
        /// <param name="key">a MIDI key in the playable range 0..119</param>
        /// <returns>the note sub-column value that plays that key</returns>
        public static byte FromKey(int key) => (byte)(key + 1);
    }
}

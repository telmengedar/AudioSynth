namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// Sole source of tracker pan math: the 0..128 byte↔signed mapping, the default per-channel layout,
    /// and the initial-pan resolver both playback paths call.
    /// </summary>
    public static class TrackerPan {

        /// <summary>Full-left endpoint of the 0..128 pan scale.</summary>
        public const byte Left = 0;

        /// <summary>Centre endpoint of the 0..128 pan scale.</summary>
        public const byte Center = 64;

        /// <summary>Full-right endpoint of the 0..128 pan scale.</summary>
        public const byte Right = 128;

        const byte DefaultHalfLeft = 32;
        const byte DefaultHalfRight = 96;

        /// <summary>
        /// Maps a 0..128 pan byte to the synth's signed scale, clamping values above <see cref="Right"/>.
        /// </summary>
        /// <param name="value">a pan byte on the 0..128 scale</param>
        /// <returns>the signed pan in [-1,+1] (0=left, 64=centre, 128=right)</returns>
        public static float ToSignedPan(byte value) {
            float pan = (value - (float)Center) / Center;
            return pan < -1f ? -1f : pan > 1f ? 1f : pan;
        }

        /// <summary>
        /// Computes the default pan byte for a channel: a single channel centres; otherwise channels
        /// alternate half-left/half-right (the shipping widen-a-dense-render layout).
        /// </summary>
        /// <param name="channelCount">the song's total channel count</param>
        /// <param name="channelIndex">the channel to compute the default for</param>
        /// <returns>the default pan byte on the 0..128 scale</returns>
        public static byte DefaultByte(int channelCount, int channelIndex) =>
            channelCount == 1 ? Center : channelIndex % 2 == 0 ? DefaultHalfLeft : DefaultHalfRight;

        /// <summary>
        /// Resolves a channel's initial signed pan: <see cref="Song.ChannelPan"/>'s entry when provided,
        /// else the computed default layout.
        /// </summary>
        /// <param name="song">the song supplying an optional per-channel initial pan</param>
        /// <param name="channel">the channel to resolve</param>
        /// <returns>the channel's initial signed pan</returns>
        public static float InitialSigned(Song song, int channel) =>
            song.ChannelPan.Length == song.ChannelCount
                ? ToSignedPan(song.ChannelPan[channel])
                : ToSignedPan(DefaultByte(song.ChannelCount, channel));
    }
}

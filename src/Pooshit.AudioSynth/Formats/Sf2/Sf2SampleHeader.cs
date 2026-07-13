namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One SF2 sample-header record (SF2 section 7.10, shdr sub-chunk): describes the location,
    /// looping points, tuning, and stereo link of a single sample in the sample-data pool.
    /// </summary>
    public sealed class Sf2SampleHeader {

        /// <summary>
        /// Creates an <see cref="Sf2SampleHeader"/>.
        /// </summary>
        public Sf2SampleHeader(
            string name,
            uint start,
            uint end,
            uint startLoop,
            uint endLoop,
            uint sampleRate,
            byte rootKey,
            sbyte pitchCorrection,
            ushort sampleLink,
            Sf2SampleLink sampleType) {
            Name = name;
            Start = start;
            End = end;
            StartLoop = startLoop;
            EndLoop = endLoop;
            SampleRate = sampleRate;
            RootKey = rootKey;
            PitchCorrection = pitchCorrection;
            SampleLink = sampleLink;
            SampleType = sampleType;
        }

        /// <summary>
        /// Sample name (up to 20 ASCII characters from achSampleName).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Index into the sample-data pool (in sample frames) of the first data point.
        /// </summary>
        public uint Start { get; }

        /// <summary>
        /// Index into the sample-data pool (in sample frames) of the first data point past the end.
        /// </summary>
        public uint End { get; }

        /// <summary>
        /// Index of the first sample frame of the loop region.
        /// </summary>
        public uint StartLoop { get; }

        /// <summary>
        /// Index of the first sample frame past the end of the loop region.
        /// </summary>
        public uint EndLoop { get; }

        /// <summary>
        /// Number of sample frames per second (e.g. 44100).
        /// </summary>
        public uint SampleRate { get; }

        /// <summary>
        /// MIDI key number at which the sample was recorded (0–127; 255 = unpitched).
        /// </summary>
        public byte RootKey { get; }

        /// <summary>
        /// Pitch correction in cents; positive shifts the sample up.
        /// </summary>
        public sbyte PitchCorrection { get; }

        /// <summary>
        /// Index of the sample header that is the stereo counterpart, or 0 if none.
        /// </summary>
        public ushort SampleLink { get; }

        /// <summary>
        /// Sample type: mono, left, right, linked, or ROM variants.
        /// </summary>
        public Sf2SampleLink SampleType { get; }

        /// <inheritdoc/>
        public override string ToString() => Name;
    }
}

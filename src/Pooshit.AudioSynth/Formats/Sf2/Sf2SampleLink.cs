namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// SF2 section 7.10 — sample type field values from the shdr sub-chunk.
    /// </summary>
    public enum Sf2SampleLink : ushort {

        /// <summary>A mono sample used on both channels or a sample with no link.</summary>
        MonoSample = 1,

        /// <summary>Right-channel sample of a stereo pair.</summary>
        RightSample = 2,

        /// <summary>Left-channel sample of a stereo pair.</summary>
        LeftSample = 4,

        /// <summary>Sample linked to another (bidirectional link).</summary>
        LinkedSample = 8,

        /// <summary>ROM mono sample.</summary>
        RomMonoSample = 0x8001,

        /// <summary>ROM right-channel sample.</summary>
        RomRightSample = 0x8002,

        /// <summary>ROM left-channel sample.</summary>
        RomLeftSample = 0x8004,

        /// <summary>ROM linked sample.</summary>
        RomLinkedSample = 0x8008
    }
}

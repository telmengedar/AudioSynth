namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// SMPTE frame rates a MIDI header's division field can encode.
    /// </summary>
    public enum SmpteFrameRate {

        /// <summary>24 frames per second.</summary>
        Smpte24 = 24,

        /// <summary>25 frames per second.</summary>
        Smpte25 = 25,

        /// <summary>29.97 (drop-frame) frames per second.</summary>
        Smpte30Drop = 29,

        /// <summary>30 frames per second.</summary>
        Smpte30 = 30
    }
}

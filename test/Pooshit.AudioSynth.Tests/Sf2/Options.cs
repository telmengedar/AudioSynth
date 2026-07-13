using System.Collections.Generic;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>Mutable knobs driving <c>Sf2TestBuilder</c>; each flag injects one malformed-input scenario.</summary>
    internal sealed class Options {
        public string RiffTag { get; set; } = "RIFF";
        public string SfbkTag { get; set; } = "sfbk";
        public bool HasOnePreset { get; set; }
        public short[]? Smpl { get; set; }
        public byte[]? Sm24 { get; set; }
        public HashSet<string>? MissingPdtaTags { get; set; }
        public bool BadPbagIndices { get; set; }
        public bool NegativeSmplSize { get; set; }

        /// <summary>Declare smpl size = MaxSafeArrayBytes+1 (no actual data); triggers the oversized-cap guard.</summary>
        public bool OversizedSmplSize { get; set; }

        /// <summary>Declare smpl size = 3 (odd); triggers the parity guard.</summary>
        public bool OddSmplSize { get; set; }

        /// <summary>Declare phdr size = MaxSafeArrayBytes+1 (no actual data); triggers ValidateChunkCount oversized guard.</summary>
        public bool OversizedPhdrSize { get; set; }

        /// <summary>Declare phdr size = 39 (not divisible by 38); triggers ValidateChunkCount parity guard.</summary>
        public bool OddPhdrSize { get; set; }

        /// <summary>Inflate the RIFF size field so ReadChunkSize passes for oversized chunk tests.</summary>
        public bool InflatedRiffSize { get; set; }

        /// <summary>Preset 0 bag index (2) > terminal bag index (0) — trips BuildPresets bagStart>bagEnd guard.</summary>
        public bool BadPhdrBagStart { get; set; }

        /// <summary>Preset terminal bag index (= pbag.Length) >= pbag.Length — trips BuildPresets bagEnd>=count guard.</summary>
        public bool BadPhdrBagEnd { get; set; }

        /// <summary>Instrument 0 bag index (2) > terminal bag index (0) — trips BuildInstruments bagStart>bagEnd guard.</summary>
        public bool BadInstBagStart { get; set; }

        /// <summary>Instrument terminal bag index (= ibag.Length) >= ibag.Length — trips BuildInstruments bagEnd>=count guard.</summary>
        public bool BadInstBagEnd { get; set; }

        /// <summary>ibag terminal declares genIdx=5 while igen has 0 real generators — trips BuildZones genEnd>gens.Length guard.</summary>
        public bool BadIbagGenEnd { get; set; }

        /// <summary>ibag terminal declares modIdx=5 while imod has 0 real modulators — trips BuildZones modEnd>mods.Length guard.</summary>
        public bool BadIbagModEnd { get; set; }

        public bool Omit(string tag) =>
            MissingPdtaTags != null && MissingPdtaTags.Contains(tag);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Builds synthetic minimal SF2 byte-array fixtures for regression tests.  All public factory
    /// methods return a <c>byte[]</c> that can be loaded by <c>Sf2SoundBankLoader</c> via a
    /// <c>MemoryStream</c>.  The internal binary format follows SF2 2.04; sizes are always computed
    /// from actual content, never hard-coded.
    /// </summary>
    internal static class Sf2TestBuilder {

        /// <summary>
        /// Builds a minimal valid SF2 with no real presets, instruments, or samples.
        /// </summary>
        public static byte[] BuildEmpty() =>
            Build(new Options());

        /// <summary>
        /// Builds a valid SF2 with one real preset backed by one instrument and one sample.
        /// </summary>
        /// <param name="smpl">16-bit sample words; defaults to four silence frames.</param>
        /// <param name="sm24">Optional LSB extension for 24-bit; must equal smpl.Length bytes if provided.</param>
        public static byte[] BuildWithOnePreset(short[]? smpl = null, byte[]? sm24 = null) =>
            Build(new Options {
                HasOnePreset = true,
                Smpl = smpl ?? new short[] { 0, 0, 0, 0 },
                Sm24 = sm24
            });

        /// <summary>
        /// Builds a fully resolvable SF2: one preset zone pointing to one instrument zone, which
        /// maps to a non-silent full-scale sample.  The SF2 is loadable and <c>StartVoice</c> will
        /// produce a live <c>SamplePlaybackVoice</c> for any key in [0, 127].
        /// </summary>
        /// <param name="smpl">sample data; defaults to 1024 full-scale (+32767) 16-bit frames.</param>
        /// <param name="sampleModes">SampleModes(54) value: 0=NoLoop, 1=Continuous.</param>
        /// <param name="overridingRootKey">OverridingRootKey(58) value; -1 to omit the generator.</param>
        /// <param name="coarseTune">CoarseTune(51) in semitones; 0 to omit the generator.</param>
        /// <param name="fineTune">FineTune(52) in cents; 0 to omit the generator.</param>
        /// <param name="keyRangeLo">key-range low bound for the instrument zone (default 0).</param>
        /// <param name="keyRangeHi">key-range high bound for the instrument zone (default 127).</param>
        public static byte[] BuildWithResolvablePreset(
            short[]? smpl = null,
            int sampleModes = 1,
            int overridingRootKey = -1,
            int coarseTune = 0,
            int fineTune = 0,
            int keyRangeLo = 0,
            int keyRangeHi = 127) {
            short[] data = smpl ?? FullScaleSample(1024);
            return Build(new Options {
                HasResolvableZones = true,
                Smpl = data,
                ResolvableSampleModes = sampleModes,
                OverridingRootKey = overridingRootKey,
                CoarseTune = coarseTune,
                FineTune = fineTune,
                KeyRangeLo = keyRangeLo,
                KeyRangeHi = keyRangeHi
            });
        }

        /// <summary>
        /// Builds a valid SF2 with one 24-bit sample whose smpl word is <paramref name="smplWord"/>
        /// and whose sm24 LSB is <paramref name="sm24Byte"/>.
        /// </summary>
        public static byte[] BuildWith24BitSample(short smplWord, byte sm24Byte) =>
            BuildWithOnePreset(
                smpl: new short[] { smplWord },
                sm24: new byte[] { sm24Byte });

        /// <summary>
        /// Builds a minimal SF2 where the smpl chunk declares a negative size.
        /// </summary>
        public static byte[] BuildNegativeSmplSize() =>
            Build(new Options { NegativeSmplSize = true });

        /// <summary>
        /// Builds a minimal SF2 missing the named pdta sub-chunk.
        /// </summary>
        public static byte[] BuildMissingPdtaChunk(string tag) =>
            Build(new Options { MissingPdtaTags = new HashSet<string>(StringComparer.Ordinal) { tag } });

        /// <summary>
        /// Builds an SF2 where pbag zone 0 has a higher generator index than the terminal bag entry
        /// (i.e., indices go backward), which must be detected by the validator.
        /// </summary>
        public static byte[] BuildBadBagIndices() =>
            Build(new Options { HasOnePreset = true, BadPbagIndices = true });

        /// <summary>
        /// Builds a byte array whose RIFF four-CC is lower-cased "riff" instead of "RIFF".
        /// </summary>
        public static byte[] BuildBadRiffTag() =>
            Build(new Options { RiffTag = "riff" });

        /// <summary>
        /// Builds a byte array whose sfbk four-CC is wrong-case "SFBK" instead of "sfbk".
        /// </summary>
        public static byte[] BuildBadSfbkTag() =>
            Build(new Options { SfbkTag = "SFBK" });

        /// <summary>
        /// Builds an SF2 where the smpl chunk declares a size exceeding the 256 MB safe-allocation cap,
        /// which must be rejected before any allocation is attempted.
        /// </summary>
        public static byte[] BuildOversizedSmplChunk() =>
            Build(new Options { OversizedSmplSize = true, InflatedRiffSize = true });

        /// <summary>
        /// Builds an SF2 where the smpl chunk declares an odd size (3), which fails the word-alignment parity guard.
        /// </summary>
        public static byte[] BuildOddSmplChunkSize() =>
            Build(new Options { OddSmplSize = true });

        /// <summary>
        /// Builds an SF2 where the phdr chunk declares a size exceeding the 256 MB safe-allocation cap.
        /// </summary>
        public static byte[] BuildOversizedPhdrChunk() =>
            Build(new Options { OversizedPhdrSize = true, InflatedRiffSize = true });

        /// <summary>
        /// Builds an SF2 where the phdr chunk declares a size (39) not divisible by the 38-byte record size.
        /// </summary>
        public static byte[] BuildOddPhdrChunkSize() =>
            Build(new Options { OddPhdrSize = true });

        /// <summary>
        /// Builds an SF2 where a preset's pbag start index (2) exceeds its end index (0) — trips the
        /// BuildPresets bagStart&gt;bagEnd guard.
        /// </summary>
        public static byte[] BuildBadPresetBagStart() =>
            Build(new Options { HasOnePreset = true, BadPhdrBagStart = true });

        /// <summary>
        /// Builds an SF2 where a preset's pbag end index equals pbag.Length — trips the
        /// BuildPresets bagEnd&gt;=count guard.
        /// </summary>
        public static byte[] BuildBadPresetBagEnd() =>
            Build(new Options { HasOnePreset = true, BadPhdrBagEnd = true });

        /// <summary>
        /// Builds an SF2 where an instrument's ibag start index (2) exceeds its end index (0) — trips the
        /// BuildInstruments bagStart&gt;bagEnd guard.
        /// </summary>
        public static byte[] BuildBadInstrumentBagStart() =>
            Build(new Options { HasOnePreset = true, BadInstBagStart = true });

        /// <summary>
        /// Builds an SF2 where an instrument's ibag end index equals ibag.Length — trips the
        /// BuildInstruments bagEnd&gt;=count guard.
        /// </summary>
        public static byte[] BuildBadInstrumentBagEnd() =>
            Build(new Options { HasOnePreset = true, BadInstBagEnd = true });

        /// <summary>
        /// Builds an SF2 where the ibag terminal claims a generator end index beyond the igen array length —
        /// trips the BuildZones genEnd&gt;gens.Length guard.
        /// </summary>
        public static byte[] BuildBadIbagGenEnd() =>
            Build(new Options { HasOnePreset = true, BadIbagGenEnd = true });

        /// <summary>
        /// Builds an SF2 where the ibag terminal claims a modulator end index beyond the imod array length —
        /// trips the BuildZones modEnd&gt;mods.Length guard.
        /// </summary>
        public static byte[] BuildBadIbagModEnd() =>
            Build(new Options { HasOnePreset = true, BadIbagModEnd = true });

        static short[] FullScaleSample(int frames) {
            short[] result = new short[frames];
            for (int i = 0; i < frames; i++)
                result[i] = 32767;
            return result;
        }

        static int CountResolvableIgen(Options opts) {
            int count = 3;
            if (opts.OverridingRootKey >= 0) count++;
            if (opts.CoarseTune != 0) count++;
            if (opts.FineTune != 0) count++;
            return count;
        }

        private const int MaxSafeArrayBytesConst = 256 * 1024 * 1024;

        static byte[] Build(Options opts) {
            byte[] info = MakeInfoList();
            byte[] sdta = MakeSdtaList(opts);
            byte[] pdta = MakePdtaList(opts);
            byte[] sfbkContent = Concat(info, sdta, pdta);

            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

            w.Write(Encoding.ASCII.GetBytes(opts.RiffTag));
            int riffSize = opts.InflatedRiffSize
                ? int.MaxValue / 2
                : sfbkContent.Length + 4;
            w.Write(riffSize);
            w.Write(Encoding.ASCII.GetBytes(opts.SfbkTag));
            w.Write(sfbkContent);
            w.Flush();
            return ms.ToArray();
        }

        static byte[] MakeInfoList() {
            byte[] ifil = MakeChunk("ifil", Concat(LE16(2), LE16(0)));
            byte[] inam = MakeChunk("inam", PadEven(Encoding.ASCII.GetBytes("Test\0")));
            return MakeList("INFO", Concat(ifil, inam));
        }

        static byte[] MakeSdtaList(Options opts) {
            byte[] smplChunk;

            if (opts.NegativeSmplSize) {
                smplChunk = Concat(
                    Encoding.ASCII.GetBytes("smpl"),
                    BitConverter.GetBytes(-1));
            } else if (opts.OversizedSmplSize) {
                smplChunk = Concat(
                    Encoding.ASCII.GetBytes("smpl"),
                    BitConverter.GetBytes(MaxSafeArrayBytesConst + 1));
            } else if (opts.OddSmplSize) {
                smplChunk = MakeChunk("smpl", new byte[] { 0, 0, 0 });
            } else {
                short[] smplData = opts.Smpl ?? Array.Empty<short>();
                byte[] smplBytes = SmplToBytes(smplData);
                smplChunk = MakeChunk("smpl", smplBytes);
            }

            if (opts.Sm24 != null) {
                byte[] sm24Chunk = MakeChunk("sm24", opts.Sm24);
                return MakeList("sdta", Concat(smplChunk, sm24Chunk));
            }

            return MakeList("sdta", smplChunk);
        }

        static byte[] MakePdtaList(Options opts) {
            int totalPbagEntries = opts.HasResolvableZones ? 2
                : (opts.HasOnePreset && !opts.BadPhdrBagStart) ? 2 : 1;
            int totalIbagEntries = opts.HasResolvableZones ? 2
                : (opts.HasOnePreset && !opts.BadInstBagStart) ? 2 : 1;

            ushort ibagTermGenIdx;
            if (opts.BadIbagGenEnd)
                ibagTermGenIdx = 5;
            else if (opts.HasResolvableZones)
                ibagTermGenIdx = (ushort)CountResolvableIgen(opts);
            else
                ibagTermGenIdx = 0;

            ushort ibagTermModIdx = opts.BadIbagModEnd ? (ushort)5 : (ushort)0;

            byte[] phdrBytes;
            if (opts.OversizedPhdrSize)
                phdrBytes = Concat(Encoding.ASCII.GetBytes("phdr"),
                                   BitConverter.GetBytes(MaxSafeArrayBytesConst + 1));
            else if (opts.OddPhdrSize)
                phdrBytes = MakeChunk("phdr", new byte[39]);
            else
                phdrBytes = MakePhdr(opts);

            byte[] pbagBytes = MakePbag(opts, totalPbagEntries);
            byte[] pmodBytes = MakePmod();
            byte[] pgenBytes = MakePgen(opts);
            byte[] instBytes = MakeInst(opts);
            byte[] ibagBytes = MakeIbag(opts, totalIbagEntries, ibagTermGenIdx, ibagTermModIdx);
            byte[] imodBytes = MakeImod();
            byte[] igenBytes = MakeIgen(opts);
            byte[] shdrBytes = MakeShdr(opts);

            List<byte[]> parts = new List<byte[]>();

            if (!opts.Omit("phdr")) parts.Add(phdrBytes);
            if (!opts.Omit("pbag")) parts.Add(pbagBytes);
            if (!opts.Omit("pmod")) parts.Add(pmodBytes);
            if (!opts.Omit("pgen")) parts.Add(pgenBytes);
            if (!opts.Omit("inst")) parts.Add(instBytes);
            if (!opts.Omit("ibag")) parts.Add(ibagBytes);
            if (!opts.Omit("imod")) parts.Add(imodBytes);
            if (!opts.Omit("igen")) parts.Add(igenBytes);
            if (!opts.Omit("shdr")) parts.Add(shdrBytes);

            return MakeList("pdta", Concat(parts.ToArray()));
        }

        static byte[] MakePhdr(Options opts) {
            if (!opts.HasOnePreset && !opts.HasResolvableZones) {
                byte[] eop = MakePresetRecord("EOP", 255, 255, 0);
                return MakeChunk("phdr", eop);
            }

            if (opts.HasResolvableZones) {
                byte[] p0 = MakePresetRecord("ResPreset", 0, 0, 0);
                byte[] terminal = MakePresetRecord("EOP", 255, 255, 1);
                return MakeChunk("phdr", Concat(p0, terminal));
            }

            if (opts.BadPhdrBagStart) {
                byte[] preset0 = MakePresetRecord("Test Preset", 0, 0, 2);
                byte[] eop = MakePresetRecord("EOP", 255, 255, 0);
                return MakeChunk("phdr", Concat(preset0, eop));
            }

            if (opts.BadPhdrBagEnd) {
                byte[] preset0 = MakePresetRecord("Test Preset", 0, 0, 0);
                byte[] eop = MakePresetRecord("EOP", 255, 255, 2);
                return MakeChunk("phdr", Concat(preset0, eop));
            }

            byte[] p = MakePresetRecord("Test Preset", 0, 0, 0);
            byte[] term = MakePresetRecord("EOP", 255, 255, 1);
            return MakeChunk("phdr", Concat(p, term));
        }

        static byte[] MakePresetRecord(string name, int patch, int bank, ushort bagIdx) =>
            Concat(Name20(name), LE16((ushort)patch), LE16((ushort)bank), LE16(bagIdx),
                   LE32(0), LE32(0), LE32(0));

        static byte[] MakePbag(Options opts, int totalEntries) {
            if (opts.HasResolvableZones) {
                byte[] zone0 = MakeBagRecord(0, 0);
                byte[] term = MakeBagRecord(1, 0);
                return MakeChunk("pbag", Concat(zone0, term));
            }

            if (!opts.HasOnePreset) {
                return MakeChunk("pbag", MakeBagRecord(0, 0));
            }

            if (opts.BadPbagIndices) {
                byte[] zone0bad = MakeBagRecord(5, 0);
                byte[] terminal = MakeBagRecord(3, 0);
                return MakeChunk("pbag", Concat(zone0bad, terminal));
            }

            byte[] zone0Std = MakeBagRecord(0, 0);
            byte[] termStd = MakeBagRecord(0, 0);
            return MakeChunk("pbag", Concat(zone0Std, termStd));
        }

        static byte[] MakeBagRecord(ushort genIdx, ushort modIdx) =>
            Concat(LE16(genIdx), LE16(modIdx));

        static byte[] MakePmod() =>
            MakeChunk("pmod", ZeroModulatorRecord());

        static byte[] MakePgen(Options opts) {
            if (opts.HasResolvableZones) {
                byte[] instrGen = MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.Instrument, 0);
                return MakeChunk("pgen", Concat(instrGen, ZeroGeneratorRecord()));
            }
            return MakeChunk("pgen", ZeroGeneratorRecord());
        }

        static byte[] MakeInst(Options opts) {
            if (opts.HasResolvableZones) {
                byte[] i0 = MakeInstRecord("ResInst", 0);
                byte[] terminal = MakeInstRecord("EOI", 1);
                return MakeChunk("inst", Concat(i0, terminal));
            }

            if (!opts.HasOnePreset) {
                byte[] eoi = MakeInstRecord("EOI", 0);
                return MakeChunk("inst", eoi);
            }

            if (opts.BadInstBagStart) {
                byte[] inst0 = MakeInstRecord("Test Inst", 2);
                byte[] eoi = MakeInstRecord("EOI", 0);
                return MakeChunk("inst", Concat(inst0, eoi));
            }

            if (opts.BadInstBagEnd) {
                byte[] inst0 = MakeInstRecord("Test Inst", 0);
                byte[] eoi = MakeInstRecord("EOI", 2);
                return MakeChunk("inst", Concat(inst0, eoi));
            }

            byte[] i0Std = MakeInstRecord("Test Inst", 0);
            byte[] termStd = MakeInstRecord("EOI", 1);
            return MakeChunk("inst", Concat(i0Std, termStd));
        }

        static byte[] MakeInstRecord(string name, ushort bagIdx) =>
            Concat(Name20(name), LE16(bagIdx));

        static byte[] MakeIbag(Options opts, int totalEntries, ushort termGenIdx, ushort termModIdx) {
            if (totalEntries == 1)
                return MakeChunk("ibag", MakeBagRecord(termGenIdx, termModIdx));
            byte[] zone0 = MakeBagRecord(0, 0);
            byte[] term = MakeBagRecord(termGenIdx, termModIdx);
            return MakeChunk("ibag", Concat(zone0, term));
        }

        static byte[] MakeImod() =>
            MakeChunk("imod", ZeroModulatorRecord());

        static byte[] MakeIgen(Options opts) {
            if (!opts.HasResolvableZones)
                return MakeChunk("igen", ZeroGeneratorRecord());

            ushort keyRangeAmount = (ushort)(opts.KeyRangeLo | (opts.KeyRangeHi << 8));
            byte[] keyRangeGen = MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.KeyRange, keyRangeAmount);
            byte[] sampleIdGen = MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.SampleID, 0);
            byte[] sampleModesGen = MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.SampleModes, (ushort)opts.ResolvableSampleModes);

            List<byte[]> gens = new List<byte[]> { keyRangeGen, sampleIdGen, sampleModesGen };

            if (opts.OverridingRootKey >= 0)
                gens.Add(MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.OverridingRootKey, (ushort)opts.OverridingRootKey));
            if (opts.CoarseTune != 0)
                gens.Add(MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.CoarseTune, (ushort)(short)opts.CoarseTune));
            if (opts.FineTune != 0)
                gens.Add(MakeGeneratorRecord((ushort)Pooshit.AudioSynth.Formats.Sf2.Sf2GeneratorType.FineTune, (ushort)(short)opts.FineTune));

            gens.Add(ZeroGeneratorRecord());

            return MakeChunk("igen", Concat(gens.ToArray()));
        }

        static byte[] MakeShdr(Options opts) {
            byte[] eos = MakeSampleRecord("EOS", 0, 0, 0, 0, 0, 0, 0, 0, 0);

            if (opts.HasResolvableZones) {
                int smplLen = opts.Smpl?.Length ?? 0;
                byte[] sample0 = MakeSampleRecord(
                    "ResSample", 0, (uint)smplLen, 0, (uint)smplLen,
                    44100, 60, 0, 0, 1);
                return MakeChunk("shdr", Concat(sample0, eos));
            }

            if (!opts.HasOnePreset)
                return MakeChunk("shdr", eos);

            byte[] sampleStd = MakeSampleRecord("TestSample", 0, 4, 0, 0, 44100, 60, 0, 0, 1);
            return MakeChunk("shdr", Concat(sampleStd, eos));
        }

        static byte[] MakeSampleRecord(
            string name,
            uint start, uint end, uint startLoop, uint endLoop,
            uint sampleRate, byte rootKey, sbyte pitchCorr,
            ushort sampleLink, ushort sampleType) =>
            Concat(
                Name20(name),
                LE32(start), LE32(end), LE32(startLoop), LE32(endLoop),
                LE32(sampleRate),
                new byte[] { rootKey, (byte)pitchCorr },
                LE16(sampleLink), LE16(sampleType));

        static byte[] ZeroModulatorRecord() => new byte[10];

        static byte[] ZeroGeneratorRecord() => new byte[4];

        static byte[] MakeGeneratorRecord(ushort genOp, ushort amount) =>
            Concat(LE16(genOp), LE16(amount));

        static byte[] MakeChunk(string tag, byte[] data) {
            byte[] header = Concat(Encoding.ASCII.GetBytes(tag), BitConverter.GetBytes(data.Length));
            return data.Length % 2 == 0
                ? Concat(header, data)
                : Concat(header, data, new byte[1]);
        }

        static byte[] MakeList(string type, byte[] content) =>
            MakeChunk("LIST", Concat(Encoding.ASCII.GetBytes(type), content));

        static byte[] SmplToBytes(short[] smpl) {
            byte[] result = new byte[smpl.Length * 2];
            for (int i = 0; i < smpl.Length; i++) {
                byte[] word = BitConverter.GetBytes(smpl[i]);
                result[i * 2] = word[0];
                result[i * 2 + 1] = word[1];
            }
            return result;
        }

        static byte[] Name20(string s) {
            byte[] b = new byte[20];
            for (int i = 0; i < s.Length && i < 20; i++) b[i] = (byte)s[i];
            return b;
        }

        static byte[] PadEven(byte[] data) =>
            data.Length % 2 == 0 ? data : Concat(data, new byte[] { 0 });

        static byte[] LE16(ushort v) => BitConverter.GetBytes(v);
        static byte[] LE32(uint v) => BitConverter.GetBytes(v);
        static byte[] LE32(int v) => BitConverter.GetBytes(v);

        static byte[] Concat(params byte[][] parts) {
            int total = 0;
            foreach (byte[] p in parts) total += p.Length;
            byte[] result = new byte[total];
            int pos = 0;
            foreach (byte[] p in parts) {
                Buffer.BlockCopy(p, 0, result, pos, p.Length);
                pos += p.Length;
            }
            return result;
        }
    }
}

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

        private const int MaxSafeArrayBytesConst = 256 * 1024 * 1024;

        private static byte[] Build(Options opts) {
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

        private static byte[] MakeInfoList() {
            byte[] ifil = MakeChunk("ifil", Concat(LE16(2), LE16(0)));
            byte[] inam = MakeChunk("inam", PadEven(Encoding.ASCII.GetBytes("Test\0")));
            return MakeList("INFO", Concat(ifil, inam));
        }

        private static byte[] MakeSdtaList(Options opts) {
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

        private static byte[] MakePdtaList(Options opts) {
            int totalPbagEntries = (opts.HasOnePreset && !opts.BadPhdrBagStart) ? 2 : 1;
            int totalIbagEntries = (opts.HasOnePreset && !opts.BadInstBagStart) ? 2 : 1;

            ushort ibagTermGenIdx = opts.BadIbagGenEnd ? (ushort)5 : (ushort)0;
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
            byte[] pgenBytes = MakePgen();
            byte[] instBytes = MakeInst(opts);
            byte[] ibagBytes = MakeIbag(totalIbagEntries, ibagTermGenIdx, ibagTermModIdx);
            byte[] imodBytes = MakeImod();
            byte[] igenBytes = MakeIgen();
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

        private static byte[] MakePhdr(Options opts) {
            if (!opts.HasOnePreset) {
                byte[] eop = MakePresetRecord("EOP", 255, 255, 0);
                return MakeChunk("phdr", eop);
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

            byte[] p0 = MakePresetRecord("Test Preset", 0, 0, 0);
            byte[] terminal = MakePresetRecord("EOP", 255, 255, 1);
            return MakeChunk("phdr", Concat(p0, terminal));
        }

        private static byte[] MakePresetRecord(string name, int patch, int bank, ushort bagIdx) =>
            Concat(Name20(name), LE16((ushort)patch), LE16((ushort)bank), LE16(bagIdx),
                   LE32(0), LE32(0), LE32(0));

        private static byte[] MakePbag(Options opts, int totalEntries) {
            if (!opts.HasOnePreset) {
                return MakeChunk("pbag", MakeBagRecord(0, 0));
            }

            if (opts.BadPbagIndices) {
                byte[] zone0bad = MakeBagRecord(5, 0);
                byte[] terminal = MakeBagRecord(3, 0);
                return MakeChunk("pbag", Concat(zone0bad, terminal));
            }

            byte[] zone0 = MakeBagRecord(0, 0);
            byte[] term = MakeBagRecord(0, 0);
            return MakeChunk("pbag", Concat(zone0, term));
        }

        private static byte[] MakeBagRecord(ushort genIdx, ushort modIdx) =>
            Concat(LE16(genIdx), LE16(modIdx));

        private static byte[] MakePmod() =>
            MakeChunk("pmod", ZeroModulatorRecord());

        private static byte[] MakePgen() =>
            MakeChunk("pgen", ZeroGeneratorRecord());

        private static byte[] MakeInst(Options opts) {
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

            byte[] i0 = MakeInstRecord("Test Inst", 0);
            byte[] terminal = MakeInstRecord("EOI", 1);
            return MakeChunk("inst", Concat(i0, terminal));
        }

        private static byte[] MakeInstRecord(string name, ushort bagIdx) =>
            Concat(Name20(name), LE16(bagIdx));

        private static byte[] MakeIbag(int totalEntries, ushort termGenIdx, ushort termModIdx) {
            if (totalEntries == 1)
                return MakeChunk("ibag", MakeBagRecord(termGenIdx, termModIdx));
            byte[] zone0 = MakeBagRecord(0, 0);
            byte[] term = MakeBagRecord(termGenIdx, termModIdx);
            return MakeChunk("ibag", Concat(zone0, term));
        }

        private static byte[] MakeImod() =>
            MakeChunk("imod", ZeroModulatorRecord());

        private static byte[] MakeIgen() =>
            MakeChunk("igen", ZeroGeneratorRecord());

        private static byte[] MakeShdr(Options opts) {
            byte[] eos = MakeSampleRecord("EOS", 0, 0, 0, 0, 0, 0, 0, 0, 0);
            if (!opts.HasOnePreset)
                return MakeChunk("shdr", eos);
            byte[] sample0 = MakeSampleRecord("TestSample", 0, 4, 0, 0, 44100, 60, 0, 0, 1);
            return MakeChunk("shdr", Concat(sample0, eos));
        }

        private static byte[] MakeSampleRecord(
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

        private static byte[] ZeroModulatorRecord() => new byte[10];

        private static byte[] ZeroGeneratorRecord() => new byte[4];

        private static byte[] MakeChunk(string tag, byte[] data) {
            byte[] header = Concat(Encoding.ASCII.GetBytes(tag), BitConverter.GetBytes(data.Length));
            return data.Length % 2 == 0
                ? Concat(header, data)
                : Concat(header, data, new byte[1]);
        }

        private static byte[] MakeList(string type, byte[] content) =>
            MakeChunk("LIST", Concat(Encoding.ASCII.GetBytes(type), content));

        private static byte[] SmplToBytes(short[] smpl) {
            byte[] result = new byte[smpl.Length * 2];
            for (int i = 0; i < smpl.Length; i++) {
                byte[] word = BitConverter.GetBytes(smpl[i]);
                result[i * 2] = word[0];
                result[i * 2 + 1] = word[1];
            }
            return result;
        }

        private static byte[] Name20(string s) {
            byte[] b = new byte[20];
            for (int i = 0; i < s.Length && i < 20; i++) b[i] = (byte)s[i];
            return b;
        }

        private static byte[] PadEven(byte[] data) =>
            data.Length % 2 == 0 ? data : Concat(data, new byte[] { 0 });

        private static byte[] LE16(ushort v) => BitConverter.GetBytes(v);
        private static byte[] LE32(uint v) => BitConverter.GetBytes(v);
        private static byte[] LE32(int v) => BitConverter.GetBytes(v);

        private static byte[] Concat(params byte[][] parts) {
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

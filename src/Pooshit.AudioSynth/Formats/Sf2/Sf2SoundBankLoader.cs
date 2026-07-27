using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// <see cref="ISoundBankLoader"/> implementation for the SF2 SoundFont 2 format.  Parses the
    /// RIFF/sfbk structure (INFO + sdta + pdta chunks) into a faithful in-memory SF2 model behind
    /// a strict untrusted-input validation boundary.
    /// </summary>
    /// <remarks>
    /// All chunk sizes and array counts derived from file data are validated before any allocation.
    /// Bag/zone indices are verified to be monotonically non-decreasing.  Any structural violation
    /// throws <see cref="InvalidSoundFontException"/> rather than producing a NullReferenceException
    /// or an OutOfMemoryException.
    /// </remarks>
    public sealed class Sf2SoundBankLoader : ISoundBankLoader {

        readonly int outputSampleRate;

        /// <summary>
        /// Creates an <see cref="Sf2SoundBankLoader"/>.
        /// </summary>
        /// <param name="outputSampleRate">
        /// Engine output sample rate stamped onto every <see cref="Sf2Patch"/>; defaults to 44100.
        /// Must match the <see cref="Pooshit.AudioSynth.Synthesis.Synthesizer"/> rate the patches will be played through.
        /// </param>
        public Sf2SoundBankLoader(int outputSampleRate = 44100) {
            if (outputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            this.outputSampleRate = outputSampleRate;
        }

        /// <inheritdoc/>
        public string FormatId => "sf2";

        /// <inheritdoc/>
        public SoundBank Load(Stream source) {
            if (source is null) throw new ArgumentNullException(nameof(source));

            if (source.CanSeek)
                return ParseSeekable(source, outputSampleRate);

            using (MemoryStream ms = new MemoryStream()) {
                source.CopyTo(ms);
                ms.Position = 0;
                return ParseSeekable(ms, outputSampleRate);
            }
        }

        static SoundBank ParseSeekable(Stream source, int rate) {
            using (BinaryReader reader = new BinaryReader(source, Encoding.ASCII, leaveOpen: true)) {
                try {
                    return ParseSoundFont(reader, rate);
                } catch (InvalidSoundFontException) {
                    throw;
                } catch (EndOfStreamException ex) {
                    throw new InvalidSoundFontException(
                        "Unexpected end of stream; the file is truncated or declares sizes exceeding its content.", ex);
                }
            }
        }

        static SoundBank ParseSoundFont(BinaryReader reader, int rate) {
            string riffTag = ReadTag(reader);
            if (riffTag != "RIFF")
                throw new InvalidSoundFontException(
                    $"Expected RIFF header tag, got '{riffTag}'. Not an SF2 file.");

            int riffSize = reader.ReadInt32();
            if (riffSize < 0)
                throw new InvalidSoundFontException(
                    $"RIFF size field is negative ({riffSize}); file is corrupt or truncated.");

            long riffEnd = reader.BaseStream.Position + riffSize;

            string sfbkTag = ReadTag(reader);
            if (sfbkTag != "sfbk")
                throw new InvalidSoundFontException(
                    $"Expected RIFF form type 'sfbk', got '{sfbkTag}'. Not an SF2 file.");

            Sf2SampleData? sampleData = null;
            Sf2PresetHeader[]? presets = null;
            Sf2Instrument[]? instruments = null;
            Sf2SampleHeader[]? sampleHeaders = null;

            while (reader.BaseStream.Position + 8 <= riffEnd) {
                string listTag = ReadTag(reader);
                int listSize = reader.ReadInt32();
                if (listSize < 0)
                    throw new InvalidSoundFontException(
                        $"LIST chunk size is negative ({listSize}) at position {reader.BaseStream.Position - 4}.");
                if (listSize < 4)
                    throw new InvalidSoundFontException(
                        $"LIST chunk size {listSize} is too small to contain a 4-byte type identifier.");

                long listDataStart = reader.BaseStream.Position;
                long listEnd = listDataStart + listSize;

                if (listTag == "LIST") {
                    string listType = ReadTag(reader);
                    long subEnd = listEnd;

                    switch (listType) {
                        case "INFO":
                            ParseInfo(reader, subEnd, riffEnd);
                            break;
                        case "sdta":
                            sampleData = ParseSdta(reader, subEnd, riffEnd);
                            break;
                        case "pdta":
                            ParsePdta(reader, subEnd, riffEnd, out presets, out instruments, out sampleHeaders);
                            break;
                        default:
                            break;
                    }
                }

                SeekPastChunk(reader, listDataStart, listSize, riffEnd);
            }

            if (sampleData is null)
                throw new InvalidSoundFontException(
                    "Missing required sdta LIST chunk; the file contains no sample data pool.");
            if (presets is null)
                throw new InvalidSoundFontException(
                    "Missing required pdta LIST chunk; preset/instrument data not found.");

            return BuildPatches(presets, instruments!, sampleHeaders!, sampleData, rate);
        }

        private static void ParseInfo(BinaryReader reader, long listEnd, long riffEnd) {
            while (reader.BaseStream.Position + 8 <= listEnd) {
                string tag = ReadTag(reader);
                int size = ReadChunkSize(reader, tag, riffEnd);
                long dataStart = reader.BaseStream.Position;
                SeekPastChunk(reader, dataStart, size, listEnd);
            }
        }

        private static Sf2SampleData ParseSdta(BinaryReader reader, long listEnd, long riffEnd) {
            short[]? smpl = null;
            byte[]? sm24 = null;

            while (reader.BaseStream.Position + 8 <= listEnd) {
                string tag = ReadTag(reader);
                int size = ReadChunkSize(reader, tag, riffEnd);
                long dataStart = reader.BaseStream.Position;

                switch (tag) {
                    case "smpl":
                        smpl = ReadSmpl(reader, size);
                        break;
                    case "sm24":
                        if (smpl != null && size == smpl.Length)
                            sm24 = reader.ReadBytes(size);
                        break;
                    default:
                        break;
                }

                SeekPastChunk(reader, dataStart, size, listEnd);
            }

            if (smpl is null)
                throw new InvalidSoundFontException(
                    "Missing required smpl sub-chunk in sdta LIST; no sample data found.");

            return sm24 != null ? new Sf2SampleData(smpl, sm24) : new Sf2SampleData(smpl);
        }

        private static short[] ReadSmpl(BinaryReader reader, int size) {
            if (size < 0)
                throw new InvalidSoundFontException(
                    $"smpl chunk size is negative ({size}); hostile or corrupt input.");
            if (size > MaxSafeArrayBytes)
                throw new InvalidSoundFontException(
                    $"smpl chunk size {size} exceeds the safe allocation limit ({MaxSafeArrayBytes} bytes).");
            if (size % 2 != 0)
                throw new InvalidSoundFontException(
                    $"smpl chunk size {size} is not a multiple of 2; 16-bit samples must be word-aligned.");

            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (size > remaining)
                throw new InvalidSoundFontException(
                    $"smpl chunk declares {size} bytes but only {remaining} bytes remain in the stream; "
                    + "file is truncated or the declared size is inflated.");

            int count = size / 2;
            short[] result = new short[count];
            for (int i = 0; i < count; i++)
                result[i] = reader.ReadInt16();
            return result;
        }

        private static void ParsePdta(
            BinaryReader reader,
            long listEnd,
            long riffEnd,
            out Sf2PresetHeader[]? presets,
            out Sf2Instrument[]? instruments,
            out Sf2SampleHeader[]? sampleHeaders) {

            RawPreset[]? phdr = null;
            RawBag[]? pbag = null;
            Sf2Modulator[]? pmod = null;
            Sf2Generator[]? pgen = null;
            RawInstrument[]? inst = null;
            RawBag[]? ibag = null;
            Sf2Modulator[]? imod = null;
            Sf2Generator[]? igen = null;
            Sf2SampleHeader[]? shdr = null;

            while (reader.BaseStream.Position + 8 <= listEnd) {
                string tag = ReadTag(reader);
                int size = ReadChunkSize(reader, tag, riffEnd);
                long dataStart = reader.BaseStream.Position;

                switch (tag) {
                    case "phdr": phdr = ReadPhdr(reader, size); break;
                    case "pbag": pbag = ReadBags(reader, size, "pbag"); break;
                    case "pmod": pmod = ReadModulators(reader, size, "pmod"); break;
                    case "pgen": pgen = ReadGenerators(reader, size, "pgen"); break;
                    case "inst": inst = ReadInst(reader, size); break;
                    case "ibag": ibag = ReadBags(reader, size, "ibag"); break;
                    case "imod": imod = ReadModulators(reader, size, "imod"); break;
                    case "igen": igen = ReadGenerators(reader, size, "igen"); break;
                    case "shdr": shdr = ReadShdr(reader, size); break;
                    default: break;
                }

                SeekPastChunk(reader, dataStart, size, listEnd);
            }

            if (phdr is null) throw new InvalidSoundFontException("Missing required 'phdr' chunk in pdta LIST.");
            if (pbag is null) throw new InvalidSoundFontException("Missing required 'pbag' chunk in pdta LIST.");
            if (pmod is null) throw new InvalidSoundFontException("Missing required 'pmod' chunk in pdta LIST.");
            if (pgen is null) throw new InvalidSoundFontException("Missing required 'pgen' chunk in pdta LIST.");
            if (inst is null) throw new InvalidSoundFontException("Missing required 'inst' chunk in pdta LIST.");
            if (ibag is null) throw new InvalidSoundFontException("Missing required 'ibag' chunk in pdta LIST.");
            if (imod is null) throw new InvalidSoundFontException("Missing required 'imod' chunk in pdta LIST.");
            if (igen is null) throw new InvalidSoundFontException("Missing required 'igen' chunk in pdta LIST.");
            if (shdr is null) throw new InvalidSoundFontException("Missing required 'shdr' chunk in pdta LIST.");

            Sf2Instrument[] builtInstruments = BuildInstruments(inst, ibag, igen, imod);
            Sf2PresetHeader[] builtPresets = BuildPresets(phdr, pbag, pgen, pmod);

            presets = builtPresets;
            instruments = builtInstruments;
            sampleHeaders = shdr;
        }

        private static RawPreset[] ReadPhdr(BinaryReader reader, int size) {
            const int RecordSize = 38;
            int count = ValidateChunkCount(size, RecordSize, 1, "phdr");
            RawPreset[] result = new RawPreset[count];
            for (int i = 0; i < count; i++) {
                string name = ReadFixedAscii(reader, 20);
                ushort patchNum = reader.ReadUInt16();
                ushort bankNum = reader.ReadUInt16();
                ushort bagIdx = reader.ReadUInt16();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                result[i] = new RawPreset(name, patchNum, bankNum, bagIdx);
            }
            return result;
        }

        private static RawInstrument[] ReadInst(BinaryReader reader, int size) {
            const int RecordSize = 22;
            int count = ValidateChunkCount(size, RecordSize, 1, "inst");
            RawInstrument[] result = new RawInstrument[count];
            for (int i = 0; i < count; i++) {
                string name = ReadFixedAscii(reader, 20);
                ushort bagIdx = reader.ReadUInt16();
                result[i] = new RawInstrument(name, bagIdx);
            }
            return result;
        }

        private static RawBag[] ReadBags(BinaryReader reader, int size, string chunkName) {
            const int RecordSize = 4;
            int count = ValidateChunkCount(size, RecordSize, 1, chunkName);
            RawBag[] bags = new RawBag[count];
            for (int i = 0; i < count; i++) {
                ushort genIdx = reader.ReadUInt16();
                ushort modIdx = reader.ReadUInt16();
                bags[i] = new RawBag(genIdx, modIdx);
            }

            for (int i = 1; i < bags.Length; i++) {
                if (bags[i].GeneratorIndex < bags[i - 1].GeneratorIndex)
                    throw new InvalidSoundFontException(
                        $"'{chunkName}' bag generator indices are not monotonically non-decreasing at entry {i}: "
                        + $"{bags[i].GeneratorIndex} < {bags[i - 1].GeneratorIndex}.");
                if (bags[i].ModulatorIndex < bags[i - 1].ModulatorIndex)
                    throw new InvalidSoundFontException(
                        $"'{chunkName}' bag modulator indices are not monotonically non-decreasing at entry {i}: "
                        + $"{bags[i].ModulatorIndex} < {bags[i - 1].ModulatorIndex}.");
            }

            return bags;
        }

        private static Sf2Generator[] ReadGenerators(BinaryReader reader, int size, string chunkName) {
            const int RecordSize = 4;
            int count = ValidateChunkCount(size, RecordSize, 1, chunkName);
            int realCount = count - 1;
            Sf2Generator[] result = new Sf2Generator[realCount];
            for (int i = 0; i < realCount; i++) {
                ushort genOp = reader.ReadUInt16();
                ushort amount = reader.ReadUInt16();
                result[i] = new Sf2Generator((Sf2GeneratorType)genOp, amount);
            }
            reader.ReadUInt16();
            reader.ReadUInt16();
            return result;
        }

        private static Sf2Modulator[] ReadModulators(BinaryReader reader, int size, string chunkName) {
            const int RecordSize = 10;
            int count = ValidateChunkCount(size, RecordSize, 1, chunkName);
            int realCount = count - 1;
            Sf2Modulator[] result = new Sf2Modulator[realCount];
            for (int i = 0; i < realCount; i++) {
                ushort srcOper = reader.ReadUInt16();
                ushort destOper = reader.ReadUInt16();
                short amount = reader.ReadInt16();
                ushort amtSrc = reader.ReadUInt16();
                ushort transform = reader.ReadUInt16();
                result[i] = new Sf2Modulator(srcOper, (Sf2GeneratorType)destOper, amount, amtSrc, (Sf2TransformType)transform);
            }
            for (int i = 0; i < 5; i++) reader.ReadUInt16();
            return result;
        }

        private static Sf2SampleHeader[] ReadShdr(BinaryReader reader, int size) {
            const int RecordSize = 46;
            int count = ValidateChunkCount(size, RecordSize, 1, "shdr");
            int realCount = count - 1;
            Sf2SampleHeader[] result = new Sf2SampleHeader[realCount];
            for (int i = 0; i < realCount; i++) {
                string name = ReadFixedAscii(reader, 20);
                uint start = reader.ReadUInt32();
                uint end = reader.ReadUInt32();
                uint startLoop = reader.ReadUInt32();
                uint endLoop = reader.ReadUInt32();
                uint sampleRate = reader.ReadUInt32();
                byte rootKey = reader.ReadByte();
                sbyte pitchCorrection = reader.ReadSByte();
                ushort sampleLink = reader.ReadUInt16();
                ushort sampleType = reader.ReadUInt16();
                result[i] = new Sf2SampleHeader(
                    name, start, end, startLoop, endLoop, sampleRate,
                    rootKey, pitchCorrection, sampleLink, (Sf2SampleLink)sampleType);
            }
            ReadShdrTerminal(reader);
            return result;
        }

        private static void ReadShdrTerminal(BinaryReader reader) {
            reader.ReadBytes(20);
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadByte();
            reader.ReadSByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
        }

        private static Sf2PresetHeader[] BuildPresets(
            RawPreset[] phdr,
            RawBag[] pbag,
            Sf2Generator[] pgen,
            Sf2Modulator[] pmod) {

            int presetCount = phdr.Length - 1;
            if (presetCount < 0)
                throw new InvalidSoundFontException("phdr has no records; expected at least one terminal EOP entry.");

            Sf2PresetHeader[] presets = new Sf2PresetHeader[presetCount];
            for (int i = 0; i < presetCount; i++) {
                int bagStart = phdr[i].BagIndex;
                int bagEnd = phdr[i + 1].BagIndex;
                if (bagStart > bagEnd)
                    throw new InvalidSoundFontException(
                        $"Preset {i} ('{phdr[i].Name}'): pbag start index {bagStart} > end index {bagEnd}.");
                if (bagEnd >= pbag.Length)
                    throw new InvalidSoundFontException(
                        $"Preset {i} ('{phdr[i].Name}'): pbag end index {bagEnd} >= pbag count {pbag.Length}.");

                Sf2Zone[] zones = BuildZones(pbag, pgen, pmod, bagStart, bagEnd, "pbag/pgen/pmod");
                presets[i] = new Sf2PresetHeader(
                    phdr[i].Name,
                    phdr[i].PatchNumber,
                    phdr[i].BankNumber,
                    zones);
            }
            return presets;
        }

        private static Sf2Instrument[] BuildInstruments(
            RawInstrument[] inst,
            RawBag[] ibag,
            Sf2Generator[] igen,
            Sf2Modulator[] imod) {

            int instCount = inst.Length - 1;
            if (instCount < 0)
                throw new InvalidSoundFontException("inst has no records; expected at least one terminal EOI entry.");

            Sf2Instrument[] result = new Sf2Instrument[instCount];
            for (int i = 0; i < instCount; i++) {
                int bagStart = inst[i].BagIndex;
                int bagEnd = inst[i + 1].BagIndex;
                if (bagStart > bagEnd)
                    throw new InvalidSoundFontException(
                        $"Instrument {i} ('{inst[i].Name}'): ibag start index {bagStart} > end index {bagEnd}.");
                if (bagEnd >= ibag.Length)
                    throw new InvalidSoundFontException(
                        $"Instrument {i} ('{inst[i].Name}'): ibag end index {bagEnd} >= ibag count {ibag.Length}.");

                Sf2Zone[] zones = BuildZones(ibag, igen, imod, bagStart, bagEnd, "ibag/igen/imod");
                result[i] = new Sf2Instrument(inst[i].Name, zones);
            }
            return result;
        }

        private static Sf2Zone[] BuildZones(
            RawBag[] bags,
            Sf2Generator[] gens,
            Sf2Modulator[] mods,
            int bagStart,
            int bagEnd,
            string context) {

            int zoneCount = bagEnd - bagStart;
            Sf2Zone[] zones = new Sf2Zone[zoneCount];
            for (int i = 0; i < zoneCount; i++) {
                int j = bagStart + i;
                int genStart = bags[j].GeneratorIndex;
                int genEnd = bags[j + 1].GeneratorIndex;
                int modStart = bags[j].ModulatorIndex;
                int modEnd = bags[j + 1].ModulatorIndex;

                if (genEnd > gens.Length)
                    throw new InvalidSoundFontException(
                        $"{context}: zone {j} generator end index {genEnd} exceeds generator count {gens.Length}.");
                if (modEnd > mods.Length)
                    throw new InvalidSoundFontException(
                        $"{context}: zone {j} modulator end index {modEnd} exceeds modulator count {mods.Length}.");

                int genCount = genEnd - genStart;
                int modCount = modEnd - modStart;

                Sf2Generator[] zoneGens = new Sf2Generator[genCount];
                if (genCount > 0)
                    Array.Copy(gens, genStart, zoneGens, 0, genCount);

                Sf2Modulator[] zoneMods = new Sf2Modulator[modCount];
                if (modCount > 0)
                    Array.Copy(mods, modStart, zoneMods, 0, modCount);

                zones[i] = new Sf2Zone(zoneGens, zoneMods);
            }
            return zones;
        }

        static SoundBank BuildPatches(
            Sf2PresetHeader[] presets,
            Sf2Instrument[] instruments,
            Sf2SampleHeader[] sampleHeaders,
            Sf2SampleData sampleData,
            int rate) {

            List<(int Bank, int Program, IPatch Patch)> entries = new List<(int, int, IPatch)>(presets.Length);
            foreach (Sf2PresetHeader preset in presets) {
                Sf2Patch patch = new Sf2Patch(preset, instruments, sampleHeaders, sampleData, rate);
                entries.Add((preset.BankNumber, preset.PatchNumber, patch));
            }
            return new SoundBank(entries);
        }

        private static int ValidateChunkCount(int size, int recordSize, int minCount, string chunkName) {
            if (size < 0)
                throw new InvalidSoundFontException(
                    $"'{chunkName}' chunk size is negative ({size}); hostile or corrupt input.");
            if (size > MaxSafeArrayBytes)
                throw new InvalidSoundFontException(
                    $"'{chunkName}' chunk size {size} exceeds the safe allocation limit ({MaxSafeArrayBytes} bytes).");
            if (size % recordSize != 0)
                throw new InvalidSoundFontException(
                    $"'{chunkName}' chunk size {size} is not a multiple of the record size {recordSize}.");
            int count = size / recordSize;
            if (count < minCount)
                throw new InvalidSoundFontException(
                    $"'{chunkName}' chunk has {count} record(s) but requires at least {minCount} (the terminal).");
            return count;
        }

        private static int ReadChunkSize(BinaryReader reader, string tag, long riffEnd) {
            int size = reader.ReadInt32();
            if (size < 0)
                throw new InvalidSoundFontException(
                    $"Chunk '{tag}' has a negative size ({size}) at position {reader.BaseStream.Position - 4}.");
            long chunkEnd = reader.BaseStream.Position + size;
            if (chunkEnd > riffEnd)
                throw new InvalidSoundFontException(
                    $"Chunk '{tag}' declared size {size} would extend past the RIFF boundary.");
            return size;
        }

        private static void SeekPastChunk(BinaryReader reader, long dataStart, int size, long parentEnd) {
            long target = dataStart + size + (size & 1L);
            if (target > parentEnd) target = parentEnd;
            reader.BaseStream.Position = target;
        }

        private static string ReadTag(BinaryReader reader) {
            byte[] bytes = reader.ReadBytes(4);
            if (bytes.Length < 4)
                throw new InvalidSoundFontException("Unexpected end of stream while reading a chunk tag.");
            return Encoding.ASCII.GetString(bytes, 0, 4);
        }

        private static string ReadFixedAscii(BinaryReader reader, int length) {
            byte[] bytes = reader.ReadBytes(length);
            int nullPos = Array.IndexOf(bytes, (byte)0);
            int strLen = nullPos < 0 ? length : nullPos;
            return Encoding.ASCII.GetString(bytes, 0, strLen);
        }

        private const int MaxSafeArrayBytes = 256 * 1024 * 1024;
    }
}

using System.Collections.Generic;
using System.IO;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression tests for <see cref="Sf2SoundBankLoader"/> covering the D-class defect catalog
    /// (DiVoid #6272): 24-bit sign extension, sm24 null-deref crash, missing-required-chunk NREs,
    /// bag-index underflow, and hostile-input allocation.  All crash-class cases are pinned with a
    /// red test BEFORE the loader was implemented; the passing tests prove the fix.
    /// </summary>
    [TestFixture]
    public class Sf2LoaderTests {

        private Sf2SoundBankLoader Loader => new Sf2SoundBankLoader();

        [Test]
        [Description("Legacy defect #6163/#6273: 24-bit max-positive sample must decode positive. "
                   + "Legacy used (x << 12) >> 12 which gives -1 for 0x7FFFFF; correct is (x << 8) >> 8.")]
        public void SampleData_24bit_MaxPositive_DecodesPositive() {
            byte[] sf2 = Sf2TestBuilder.BuildWith24BitSample(smplWord: 0x7FFF, sm24Byte: 0xFF);

            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));
            Sf2Patch patch = (Sf2Patch)patches[0];

            int decoded = patch.SampleData.GetSample(0);

            Assert.That(decoded, Is.EqualTo(0x7FFFFF),
                $"Expected 0x7FFFFF = 8388607 (max positive 24-bit), got {decoded}. "
                + "The legacy bug (<<12>>12) produces -1 here.");
            Assert.That(decoded, Is.GreaterThan(0),
                "Max-positive 24-bit sample must be positive after correct sign extension.");
        }

        [Test]
        [Description("Legacy defect #6163/#6274: min-negative 24-bit sample must decode negative.")]
        public void SampleData_24bit_MinNegative_DecodesNegative() {
            byte[] sf2 = Sf2TestBuilder.BuildWith24BitSample(
                smplWord: unchecked((short)0x8000),
                sm24Byte: 0x00);

            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));
            Sf2Patch patch = (Sf2Patch)patches[0];

            int decoded = patch.SampleData.GetSample(0);

            Assert.That(decoded, Is.EqualTo(unchecked((int)0xFF800000)),
                "Min-negative 24-bit sample (0x800000) should sign-extend to -8388608.");
            Assert.That(decoded, Is.LessThan(0));
        }

        [Test]
        [Description("Legacy defect #6163: sm24 chunk must load without NullReferenceException "
                   + "(the legacy read samples.Length before samples was assigned).")]
        public void Loader_Sm24Chunk_LoadsWithoutException() {
            byte[] sf2 = Sf2TestBuilder.BuildWith24BitSample(smplWord: 0x1234, sm24Byte: 0x56);

            Assert.DoesNotThrow(() => Loader.Load(new MemoryStream(sf2)),
                "Loading an SF2 with an sm24 chunk must not throw.");
        }

        [Test]
        [Description("Verify 24-bit sample data correctly exposes BitsPerSample = 24.")]
        public void SampleData_24bit_BitsPerSampleIs24() {
            byte[] sf2 = Sf2TestBuilder.BuildWith24BitSample(smplWord: 0x0100, sm24Byte: 0x00);

            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));
            Sf2Patch patch = (Sf2Patch)patches[0];

            Assert.That(patch.SampleData.BitsPerSample, Is.EqualTo(24));
        }

        [TestCase("phdr", Description = "Legacy defect #6168: missing phdr must not NRE")]
        [TestCase("pbag", Description = "Legacy defect #6168: missing pbag must not NRE")]
        [TestCase("pmod", Description = "Legacy defect #6239: missing pmod must not NRE")]
        [TestCase("pgen", Description = "Legacy defect #6239: missing pgen must not NRE")]
        [TestCase("inst", Description = "Legacy defect #6168: missing inst must not NRE")]
        [TestCase("ibag", Description = "Legacy defect #6239: missing ibag must not NRE")]
        [TestCase("imod", Description = "Legacy defect #6239: missing imod must not NRE")]
        [TestCase("igen", Description = "Legacy defect #6239: missing igen must not NRE")]
        [TestCase("shdr", Description = "Legacy defect #6239: missing shdr must not NRE")]
        public void Loader_MissingRequiredPdtaChunk_ThrowsInvalidSoundFontException(string missingTag) {
            byte[] sf2 = Sf2TestBuilder.BuildMissingPdtaChunk(missingTag);

            Assert.Throws<InvalidSoundFontException>(
                () => Loader.Load(new MemoryStream(sf2)),
                $"Missing '{missingTag}' chunk must throw InvalidSoundFontException, not NullReferenceException.");
        }

        [Test]
        [Description("Legacy defect #6228: bag indices going backward must throw a clean exception, "
                   + "not underflow into Array.Copy.")]
        public void Loader_BadBagIndices_ThrowsInvalidSoundFontException() {
            byte[] sf2 = Sf2TestBuilder.BuildBadBagIndices();

            Assert.Throws<InvalidSoundFontException>(
                () => Loader.Load(new MemoryStream(sf2)),
                "Decreasing bag generator indices must throw InvalidSoundFontException, not produce wrong results.");
        }

        [Test]
        [Description("Legacy defect #6221: hostile smpl chunk declaring size = -1 must throw a clean "
                   + "typed exception rather than OutOfMemoryException or NegativeArraySizeException.")]
        public void Loader_NegativeSmplChunkSize_ThrowsInvalidSoundFontException() {
            byte[] sf2 = Sf2TestBuilder.BuildNegativeSmplSize();

            Assert.Throws<InvalidSoundFontException>(
                () => Loader.Load(new MemoryStream(sf2)),
                "A negative smpl chunk size must throw InvalidSoundFontException, not OOM or crash.");
        }

        [Test]
        [Description("RIFF tag must match exactly 'RIFF' (case-sensitive); 'riff' is not valid.")]
        public void Loader_LowercaseRiffTag_ThrowsInvalidSoundFontException() {
            byte[] sf2 = Sf2TestBuilder.BuildBadRiffTag();

            Assert.Throws<InvalidSoundFontException>(
                () => Loader.Load(new MemoryStream(sf2)),
                "Lower-case 'riff' header must be rejected with InvalidSoundFontException.");
        }

        [Test]
        [Description("sfbk type tag must match exactly 'sfbk' (case-sensitive); 'SFBK' is not valid.")]
        public void Loader_WrongSfbkTag_ThrowsInvalidSoundFontException() {
            byte[] sf2 = Sf2TestBuilder.BuildBadSfbkTag();

            Assert.Throws<InvalidSoundFontException>(
                () => Loader.Load(new MemoryStream(sf2)),
                "Wrong-case 'sfbk' type must be rejected with InvalidSoundFontException.");
        }

        [Test]
        [Description("A valid minimal SF2 with no real presets must load without error and return an empty patch list.")]
        public void Loader_EmptySoundFont_LoadsSuccessfullyAndReturnsNoPatch() {
            byte[] sf2 = Sf2TestBuilder.BuildEmpty();

            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));

            Assert.That(patches, Is.Empty,
                "A SoundFont with no presets must return an empty patch list.");
        }

        [Test]
        [Description("A valid SF2 with one preset must load successfully and return exactly one Sf2Patch.")]
        public void Loader_SoundFontWithOnePreset_ReturnsOneSf2Patch() {
            byte[] sf2 = Sf2TestBuilder.BuildWithOnePreset();

            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));

            Assert.That(patches, Has.Count.EqualTo(1),
                "A SoundFont with one preset must return exactly one patch.");
            Assert.That(patches[0], Is.InstanceOf<Sf2Patch>(),
                "The returned patch must be an Sf2Patch.");
        }

        [Test]
        [Description("The loader's FormatId property must return 'sf2'.")]
        public void Loader_FormatId_IsSf2() {
            Assert.That(Loader.FormatId, Is.EqualTo("sf2"));
        }

        [Test]
        [Description("Sf2Patch.StartVoice must throw NotImplementedException (voice engine is a future PR).")]
        public void Sf2Patch_StartVoice_ThrowsNotImplementedException() {
            byte[] sf2 = Sf2TestBuilder.BuildWithOnePreset();
            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));
            IPatch patch = patches[0];

            Assert.Throws<System.NotImplementedException>(
                () => patch.StartVoice(60, 100),
                "StartVoice must throw NotImplementedException until the voice engine is implemented.");
        }

        [Test]
        [Description("16-bit sample pool must have BitsPerSample = 16 and GetSample must return the int16 value.")]
        public void SampleData_16bit_RoundTrips() {
            short[] smpl = new short[] { 100, -200, 32767, -32768 };
            byte[] sf2 = Sf2TestBuilder.BuildWithOnePreset(smpl: smpl);

            IReadOnlyList<IPatch> patches = Loader.Load(new MemoryStream(sf2));
            Sf2Patch patch = (Sf2Patch)patches[0];

            Assert.That(patch.SampleData.BitsPerSample, Is.EqualTo(16));
            Assert.That(patch.SampleData.GetSample(0), Is.EqualTo(100));
            Assert.That(patch.SampleData.GetSample(1), Is.EqualTo(-200));
            Assert.That(patch.SampleData.GetSample(2), Is.EqualTo(32767));
            Assert.That(patch.SampleData.GetSample(3), Is.EqualTo(-32768));
        }

        [Test]
        [Description("Loading from a non-seekable stream must succeed by buffering internally.")]
        public void Loader_NonSeekableStream_LoadsSuccessfully() {
            byte[] sf2 = Sf2TestBuilder.BuildEmpty();

            using NonSeekableStream ns = new NonSeekableStream(sf2);
            IReadOnlyList<IPatch> patches = Loader.Load(ns);

            Assert.That(patches, Is.Empty, "Loading from a non-seekable stream must succeed.");
        }

        private sealed class NonSeekableStream : System.IO.Stream {
            private readonly System.IO.MemoryStream _inner;
            public NonSeekableStream(byte[] data) => _inner = new System.IO.MemoryStream(data);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new System.NotSupportedException();
            public override long Position { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new System.NotSupportedException();
            public override void SetLength(long value) => throw new System.NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
    }
}

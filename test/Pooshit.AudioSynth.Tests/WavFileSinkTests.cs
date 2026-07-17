using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Audio.Sources;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Coverage for <see cref="WavFileSink"/>: the float-to-PCM16 narrowing choke point and the
    /// RIFF/WAVE header it writes. Assertions read raw bytes rather than a re-parsed WAV object.
    /// </summary>
    public class WavFileSinkTests {

        const int HeaderLength = 44;

        static string ReadTag(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);

        [Test]
        public void Constructor_NullStream_ThrowsArgumentNullException() {
            Assert.Throws<ArgumentNullException>(() => new WavFileSink((Stream)null!, new AudioFormat(44100, 2)));
        }

        [Test]
        public void Constructor_NonWritableStream_ThrowsArgumentException() {
            using MemoryStream readOnly = new MemoryStream(new byte[4], writable: false);
            Assert.Throws<ArgumentException>(() => new WavFileSink(readOnly, new AudioFormat(44100, 2)));
        }

        [Test]
        public void Constructor_NonSeekableWritableStream_ThrowsArgumentException() {
            using WriteOnlyNonSeekableStream stream = new WriteOnlyNonSeekableStream();
            Assert.Throws<ArgumentException>(() => new WavFileSink(stream, new AudioFormat(44100, 2)));
        }

        [Test]
        public void Header_ContainsValidRiffWaveTagsAndFormatFieldsForNegotiatedFormat() {
            AudioFormat format = new AudioFormat(48000, 2);
            MemoryStream stream = new MemoryStream();
            float[] frames = { 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f };

            using (WavFileSink sink = new WavFileSink(stream, format, leaveOpen: true))
                sink.Write(frames);

            byte[] bytes = stream.ToArray();
            int expectedDataBytes = frames.Length * 2;

            Assert.That(bytes.Length, Is.EqualTo(HeaderLength + expectedDataBytes));
            Assert.That(ReadTag(bytes, 0), Is.EqualTo("RIFF"));
            Assert.That(ReadTag(bytes, 8), Is.EqualTo("WAVE"));
            Assert.That(ReadTag(bytes, 12), Is.EqualTo("fmt "));
            Assert.That(ReadTag(bytes, 36), Is.EqualTo("data"));

            uint riffChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
            Assert.That(riffChunkSize, Is.EqualTo((uint)(HeaderLength - 8 + expectedDataBytes)));

            uint fmtChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            Assert.That(fmtChunkSize, Is.EqualTo(16u));

            ushort audioFormatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
            Assert.That(audioFormatTag, Is.EqualTo((ushort)1), "PCM audio format tag expected.");

            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
            Assert.That(channels, Is.EqualTo((ushort)format.Channels));

            uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
            Assert.That(sampleRate, Is.EqualTo((uint)format.SampleRate));

            uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));
            Assert.That(byteRate, Is.EqualTo((uint)(format.SampleRate * format.Channels * 2)));

            ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32, 2));
            Assert.That(blockAlign, Is.EqualTo((ushort)(format.Channels * 2)));

            ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2));
            Assert.That(bitsPerSample, Is.EqualTo((ushort)16));

            uint dataChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4));
            Assert.That(dataChunkSize, Is.EqualTo((uint)expectedDataBytes));
        }

        [Test]
        public void Header_AccumulatesDataSizeAcrossMultipleWriteCalls() {
            AudioFormat format = new AudioFormat(44100, 1);
            MemoryStream stream = new MemoryStream();

            using (WavFileSink sink = new WavFileSink(stream, format, leaveOpen: true)) {
                sink.Write(new float[] { 0.1f, 0.2f, 0.3f });
                sink.Write(new float[] { 0.4f, 0.5f });
            }

            byte[] bytes = stream.ToArray();
            uint dataChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4));
            Assert.That(dataChunkSize, Is.EqualTo((uint)(5 * 2)));
            Assert.That(bytes.Length, Is.EqualTo(HeaderLength + 5 * 2));
        }

        [Test]
        public void DataPayload_RenderedSineIsNonSilentAndAmplitudeBoundedInRawBytes() {
            AudioFormat format = new AudioFormat(44100, 1);
            SineSource source = new SineSource(format, 440.0, 0.5f);
            MemoryStream stream = new MemoryStream();

            using (WavFileSink sink = new WavFileSink(stream, format, leaveOpen: true))
                OfflineRenderer.Render(source, sink, 4096);

            byte[] bytes = stream.ToArray();
            ReadOnlySpan<byte> data = bytes.AsSpan(HeaderLength);
            Assert.That(data.Length % 2, Is.EqualTo(0));

            int sampleCount = data.Length / 2;
            short peak = 0;
            bool hasNonZero = false;
            for (int i = 0; i < sampleCount; i++) {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
                if (sample != 0)
                    hasNonZero = true;
                peak = Math.Max(peak, Math.Abs(sample));
            }

            Assert.That(hasNonZero, Is.True, "rendered PCM data was entirely silent");
            Assert.That(peak, Is.GreaterThan((short)(0.4f * short.MaxValue)),
                $"expected an audible full-scale-narrowed sine, peak was {peak}");
            Assert.That(peak, Is.LessThanOrEqualTo((short)(0.5f * short.MaxValue + 10)),
                $"amplitude exceeded the requested bound after narrowing, peak was {peak}");
        }

        [Test]
        public void Write_ClampsOutOfRangeSamplesToPcm16FullScaleBeforeNarrowing() {
            AudioFormat format = new AudioFormat(44100, 1);
            MemoryStream stream = new MemoryStream();
            float[] samples = { 2f, -2f, 1f, -1f, 0f, float.NaN };

            using (WavFileSink sink = new WavFileSink(stream, format, leaveOpen: true))
                sink.Write(samples);

            byte[] bytes = stream.ToArray();
            short[] narrowed = new short[samples.Length];
            for (int i = 0; i < narrowed.Length; i++)
                narrowed[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(HeaderLength + i * 2, 2));

            Assert.That(narrowed[0], Is.EqualTo(short.MaxValue), "sample above +1 must clamp to full scale");
            Assert.That(narrowed[1], Is.EqualTo((short)-short.MaxValue), "sample below -1 must clamp to full scale");
            Assert.That(narrowed[2], Is.EqualTo(short.MaxValue));
            Assert.That(narrowed[3], Is.EqualTo((short)-short.MaxValue));
            Assert.That(narrowed[4], Is.EqualTo((short)0));
            Assert.That(narrowed[5], Is.EqualTo((short)0), "NaN must be neutralized to silence, not narrowed as garbage");
        }

        [Test]
        public void Dispose_IsIdempotentAndDoesNotThrowOnSecondCall() {
            MemoryStream stream = new MemoryStream();
            WavFileSink sink = new WavFileSink(stream, new AudioFormat(44100, 1), leaveOpen: true);
            sink.Write(new float[] { 0.1f });

            sink.Dispose();
            Assert.DoesNotThrow(() => sink.Dispose());
        }

        [Test]
        public void Dispose_WithLeaveOpenFalse_ClosesUnderlyingStream() {
            MemoryStream stream = new MemoryStream();
            WavFileSink sink = new WavFileSink(stream, new AudioFormat(44100, 1));
            sink.Write(new float[] { 0.1f });

            sink.Dispose();

            Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(0));
        }

        [Test]
        public void Dispose_WithLeaveOpenTrue_KeepsUnderlyingStreamOpen() {
            MemoryStream stream = new MemoryStream();
            WavFileSink sink = new WavFileSink(stream, new AudioFormat(44100, 1), leaveOpen: true);
            sink.Write(new float[] { 0.1f });

            sink.Dispose();

            Assert.DoesNotThrow(() => stream.WriteByte(0));
        }

        [Test]
        public void PathConstructor_WritesAWellFormedWavFileToDisk() {
            string path = Path.Combine(Path.GetTempPath(), $"wavfilesink-{Guid.NewGuid():N}.wav");
            AudioFormat format = new AudioFormat(22050, 1);
            try {
                using (WavFileSink sink = new WavFileSink(path, format))
                    sink.Write(new float[] { 0.25f, -0.25f, 0.5f });

                byte[] bytes = File.ReadAllBytes(path);
                Assert.That(bytes.Length, Is.EqualTo(HeaderLength + 3 * 2));
                Assert.That(ReadTag(bytes, 0), Is.EqualTo("RIFF"));
                Assert.That(ReadTag(bytes, 8), Is.EqualTo("WAVE"));
            } finally {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void Format_ReturnsFormatSuppliedAtConstruction() {
            AudioFormat format = new AudioFormat(96000, 2);
            using MemoryStream stream = new MemoryStream();
            using WavFileSink sink = new WavFileSink(stream, format, leaveOpen: true);

            Assert.That(sink.Format, Is.EqualTo(format));
        }
    }
}

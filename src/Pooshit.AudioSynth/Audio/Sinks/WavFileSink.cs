using System;
using System.Buffers.Binary;
using System.IO;

namespace Pooshit.AudioSynth.Audio.Sinks {

    /// <summary>
    /// Sink that narrows the interleaved 32-bit float stream to 16-bit PCM and writes it to a seekable
    /// stream as a canonical RIFF/WAVE file (fmt + data chunks); header sizes are patched on <see cref="Dispose"/>.
    /// </summary>
    public sealed class WavFileSink : IAudioSink, IDisposable {

        const int BitsPerSample = 16;
        const int BytesPerSample = BitsPerSample / 8;
        const int HeaderLength = 44;

        readonly Stream stream;
        readonly bool leaveOpen;
        long dataBytesWritten;
        bool disposed;

        /// <summary>
        /// Creates a <see cref="WavFileSink"/> that writes to a new file at <paramref name="path"/>.
        /// </summary>
        /// <param name="path">destination WAV file path; overwritten if it already exists</param>
        /// <param name="format">sample rate and channel count negotiated with the upstream source</param>
        public WavFileSink(string path, AudioFormat format) : this(File.Create(path), format) {
        }

        /// <summary>
        /// Creates a <see cref="WavFileSink"/> that writes to an already-open seekable stream.
        /// </summary>
        /// <param name="stream">destination stream; must support writing and seeking so the header can be patched on close</param>
        /// <param name="format">sample rate and channel count negotiated with the upstream source</param>
        /// <param name="leaveOpen">when true, <paramref name="stream"/> is not disposed by this sink</param>
        public WavFileSink(Stream stream, AudioFormat format, bool leaveOpen = false) {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite)
                throw new ArgumentException("Stream must be writable.", nameof(stream));
            if (!stream.CanSeek)
                throw new ArgumentException("Stream must be seekable so the RIFF header can be patched once the data length is known.", nameof(stream));
            Format = format;
            this.leaveOpen = leaveOpen;
            WriteHeaderPlaceholder();
        }

        /// <inheritdoc/>
        public AudioFormat Format { get; }

        /// <inheritdoc/>
        public void Write(ReadOnlySpan<float> source) {
            byte[] buffer = new byte[source.Length * BytesPerSample];
            for (int i = 0; i < source.Length; i++) {
                short pcm = ToPcm16(source[i]);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * BytesPerSample, BytesPerSample), pcm);
            }
            stream.Write(buffer, 0, buffer.Length);
            dataBytesWritten += buffer.Length;
        }

        /// <summary>
        /// Patches the RIFF and data chunk sizes with the final byte counts and closes the underlying stream unless <c>leaveOpen</c> was requested.
        /// </summary>
        public void Dispose() {
            if (disposed)
                return;
            disposed = true;
            PatchHeaderSizes();
            if (!leaveOpen)
                stream.Dispose();
        }

        static short ToPcm16(float sample) {
            if (float.IsNaN(sample))
                return 0;
            float clamped = sample < -1f ? -1f : sample > 1f ? 1f : sample;
            return (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
        }

        void WriteHeaderPlaceholder() {
            byte[] header = new byte[HeaderLength];
            WriteHeader(header, 0);
            stream.Write(header, 0, header.Length);
        }

        void PatchHeaderSizes() {
            byte[] header = new byte[HeaderLength];
            WriteHeader(header, dataBytesWritten);
            long originalPosition = stream.Position;
            stream.Position = 0;
            stream.Write(header, 0, header.Length);
            stream.Position = originalPosition;
            stream.Flush();
        }

        void WriteHeader(byte[] header, long dataLength) {
            int byteRate = Format.SampleRate * Format.Channels * BytesPerSample;
            int blockAlign = Format.Channels * BytesPerSample;
            uint riffChunkSize = (uint)(HeaderLength - 8 + dataLength);

            WriteTag(header, 0, "RIFF");
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), riffChunkSize);
            WriteTag(header, 8, "WAVE");
            WriteTag(header, 12, "fmt ");
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), (ushort)Format.Channels);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), (uint)Format.SampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), (uint)byteRate);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), (ushort)blockAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34, 2), BitsPerSample);
            WriteTag(header, 36, "data");
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), (uint)dataLength);
        }

        static void WriteTag(byte[] buffer, int offset, string tag) {
            for (int i = 0; i < 4; i++)
                buffer[offset + i] = (byte)tag[i];
        }
    }
}

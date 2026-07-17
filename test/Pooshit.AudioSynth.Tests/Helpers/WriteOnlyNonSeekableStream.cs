using System;
using System.IO;

namespace Pooshit.AudioSynth.Tests.Helpers {

    /// <summary>
    /// Writable, non-seekable stream used to exercise <see cref="Pooshit.AudioSynth.Audio.Sinks.WavFileSink"/>'s seek-required guard.
    /// </summary>
    internal sealed class WriteOnlyNonSeekableStream : Stream {

        readonly MemoryStream inner = new MemoryStream();

        /// <inheritdoc/>
        public override bool CanRead => false;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => true;

        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void Flush() => inner.Flush();

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        /// <inheritdoc/>
        protected override void Dispose(bool disposing) {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

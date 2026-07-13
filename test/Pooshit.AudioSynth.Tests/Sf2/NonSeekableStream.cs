namespace Pooshit.AudioSynth.Tests {

    /// <summary>Read-only, non-seekable stream wrapper used to exercise the loader's forward-only path.</summary>
    internal sealed class NonSeekableStream : System.IO.Stream {
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

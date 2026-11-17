using System.Buffers;

namespace BifrostQL.Server
{
    /// <summary>
    /// A write-only stream that grows through <see cref="ArrayPool{T}"/> rentals and hands back
    /// exactly what was written as one array. A <see cref="MemoryStream"/> grows by allocating a
    /// doubled array each time, so a large response passes through a chain of large-object-heap
    /// arrays before <see cref="MemoryStream.ToArray"/> copies it once more; this stream's
    /// growth buffers go back to the pool instead. Dispose returns the last rental.
    /// </summary>
    internal sealed class PooledBufferStream : Stream
    {
        private const int InitialCapacity = 16 * 1024;

        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        private int _length;
        private bool _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// The bytes written so far, as a view of the pooled buffer: valid until the next write
        /// or until the stream is disposed.
        /// </summary>
        public ArraySegment<byte> Written
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new ArraySegment<byte>(_buffer, 0, _length);
            }
        }

        /// <summary>A new array holding exactly the bytes written so far.</summary>
        public byte[] ToArray()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer.AsSpan(0, _length).ToArray();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureCapacity(source.Length);
            source.CopyTo(_buffer.AsSpan(_length));
            _length += source.Length;
        }

        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(source.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void EnsureCapacity(int additional)
        {
            var required = (long)_length + additional;
            if (required <= _buffer.Length)
                return;
            if (required > Array.MaxLength)
                throw new IOException($"A buffered response cannot exceed {Array.MaxLength} bytes.");

            var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(required, (long)_buffer.Length * 2), Array.MaxLength));
            _buffer.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = Array.Empty<byte>();
            }
            base.Dispose(disposing);
        }
    }
}

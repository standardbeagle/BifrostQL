using System.Buffers;
using System.IO.Pipelines;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Adapts a Kestrel connection's <see cref="IDuplexPipe"/> transport to a byte
    /// <see cref="Stream"/>. The pgwire handshake logic is written against a plain
    /// <see cref="Stream"/> so it can be driven over a real socket in tests and wrapped
    /// by <see cref="System.Net.Security.SslStream"/> after the SSLRequest upgrade; this
    /// adapter is the only Kestrel-specific glue between the two. Reads pull from the
    /// pipe reader; writes buffer in the pipe writer until the codec's explicit flush at a
    /// message boundary, or until <see cref="MaxUnflushedBytes"/> accumulate.
    ///
    /// <para>The adapter is async only. The synchronous <see cref="Read(byte[], int, int)"/>,
    /// <see cref="Write(byte[], int, int)"/> and <see cref="Flush"/> throw rather than block a
    /// thread-pool thread on the pipe, and the array overloads of <c>ReadAsync</c> /
    /// <c>WriteAsync</c> forward to the memory ones instead of the base class's fallback to the
    /// synchronous members.</para>
    /// </summary>
    internal sealed class DuplexPipeStream : Stream
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;
        private int _unflushedBytes;

        /// <summary>Unflushed bytes after which a write flushes on its own.</summary>
        internal const int MaxUnflushedBytes = 64 * 1024;

        public DuplexPipeStream(IDuplexPipe pipe)
        {
            ArgumentNullException.ThrowIfNull(pipe);
            _reader = pipe.Input;
            _writer = pipe.Output;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;

            var result = await _reader.ReadAsync(cancellationToken);
            var pending = result.Buffer;
            if (pending.IsEmpty && result.IsCompleted)
            {
                _reader.AdvanceTo(pending.End);
                return 0; // peer closed
            }

            var toCopy = (int)Math.Min(buffer.Length, pending.Length);
            pending.Slice(0, toCopy).CopyTo(buffer.Span);
            // Consume only what we handed out; the rest stays buffered for the next read.
            _reader.AdvanceTo(pending.GetPosition(toCopy));
            return toCopy;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writer.Write(buffer.Span);
            _unflushedBytes += buffer.Length;
            // Writes accumulate in the pipe until the caller's explicit FlushAsync (every codec
            // flushes at its own message boundary), so a result set of N rows costs one flush,
            // not N. The bound keeps a large unflushed result from growing the pipe without limit
            // and hands the transport's backpressure to the writer.
            if (_unflushedBytes >= MaxUnflushedBytes)
                await FlushAsync(cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            _unflushedBytes = 0;
            await _writer.FlushAsync(cancellationToken);
        }

        public override void Flush() => throw SyncIoNotSupported();

        private static NotSupportedException SyncIoNotSupported()
            => new("DuplexPipeStream is async only; use ReadAsync, WriteAsync and FlushAsync.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

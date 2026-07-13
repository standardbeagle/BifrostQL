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
    /// pipe reader; writes push to the pipe writer (flush-through, since the protocol
    /// flushes explicitly after each frame).
    /// </summary>
    internal sealed class DuplexPipeStream : Stream
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

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

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writer.Write(buffer.Span);
            await _writer.FlushAsync(cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
            => await _writer.FlushAsync(cancellationToken);

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

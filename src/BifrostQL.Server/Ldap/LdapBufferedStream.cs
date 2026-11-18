namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// A read-buffering wrapper around the LDAP connection's transport. It exists for two reasons,
    /// one of them load-bearing for transport security:
    ///
    /// <list type="bullet">
    /// <item><b>Pipelined plaintext is observable.</b> The message reader takes the BER envelope off
    /// the wire a byte at a time. Reading straight from the socket, anything the peer pipelined
    /// BEHIND the current message stays in the kernel where this process cannot see it, so a
    /// "did the peer send more than the StartTLS request?" check would be structurally incapable of
    /// ever firing. Buffering the socket read here means the bytes that arrived with the current
    /// message are held in <see cref="BufferedByteCount"/>, which the StartTLS handler checks before
    /// it agrees to negotiate (RFC 4511 §4.14.3.1).</item>
    /// <item><b>One syscall per burst instead of one per byte.</b> The framing reader's per-byte
    /// reads now hit this buffer.</item>
    /// </list>
    ///
    /// <para>Only the READ side is buffered: responses are written whole and flushed explicitly, so
    /// writes pass straight through and no response can be stranded in a write buffer at close.
    /// The wrapper over the raw transport does not own it — the connection handler that created
    /// the transport disposes it — so an upgraded session can hand the same socket to
    /// <see cref="System.Net.Security.SslStream"/> without a double-dispose race. The wrapper over
    /// a StartTLS <see cref="System.Net.Security.SslStream"/> does own it: nothing else holds that
    /// stream, so disposing it here is what sends close_notify and frees the TLS session.</para>
    /// </summary>
    internal sealed class LdapBufferedStream : Stream
    {
        /// <summary>Default read-buffer size: one burst of LDAP traffic, well under the message cap.</summary>
        public const int DefaultBufferSize = 8192;

        private readonly Stream _inner;
        private readonly bool _ownsInner;
        private readonly byte[] _buffer;
        private int _start;
        private int _end;

        public LdapBufferedStream(Stream inner, int bufferSize = DefaultBufferSize, bool ownsInner = false)
        {
            ArgumentNullException.ThrowIfNull(inner);
            if (bufferSize < 1)
                throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "buffer size must be positive.");
            _inner = inner;
            _ownsInner = ownsInner;
            _buffer = new byte[bufferSize];
        }

        /// <summary>
        /// How many bytes have been read off the transport but not yet consumed by the framing
        /// reader. Nonzero immediately after a complete message means the peer pipelined more data
        /// behind it — which the StartTLS handler treats as a fatal protocol error rather than
        /// carrying plaintext across a transport upgrade.
        /// </summary>
        public int BufferedByteCount => _end - _start;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
                return 0;
            if (_start == _end)
            {
                _start = _end = 0;
                var filled = await _inner.ReadAsync(_buffer, cancellationToken);
                if (filled == 0)
                    return 0; // end of stream
                _end = filled;
            }
            return TakeBuffered(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count == 0)
                return 0;
            if (_start == _end)
            {
                _start = _end = 0;
                var filled = _inner.Read(_buffer, 0, _buffer.Length);
                if (filled == 0)
                    return 0;
                _end = filled;
            }
            return TakeBuffered(buffer.AsSpan(offset, count));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int TakeBuffered(Span<byte> destination)
        {
            var take = Math.Min(destination.Length, _end - _start);
            var consumed = _buffer.AsSpan(_start, take);
            consumed.CopyTo(destination);
            // Consumed bytes are wiped as they leave: the buffer lives for the whole connection and
            // a bind's password passed through it, so without this the cleartext stayed here until
            // the next burst happened to overwrite it.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(consumed);
            _start += take;
            if (_start == _end)
                _start = _end = 0;
            return take;
        }

        /// <summary>
        /// Closes an owned inner stream. An owned <see cref="System.Net.Security.SslStream"/> is shut
        /// down first, which writes close_notify, so the peer sees an orderly TLS close instead of a
        /// truncation. The shutdown writes through the bounded transport underneath, so a peer that
        /// does not read cannot hold it past the write timeout. A peer already gone is not an
        /// error at close.
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            if (_ownsInner)
            {
                if (_inner is System.Net.Security.SslStream ssl)
                {
                    try
                    {
                        await ssl.ShutdownAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                                  or InvalidOperationException or OperationCanceledException)
                    {
                        // The transport is gone or cut by its deadline; dispose still frees the session.
                    }
                }
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        // ---- write side: straight through ----
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

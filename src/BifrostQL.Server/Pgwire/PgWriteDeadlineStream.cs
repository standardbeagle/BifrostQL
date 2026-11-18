namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Bounds every write of an AUTHENTICATED pgwire session by
    /// <see cref="PgWireOptions.WriteTimeout"/>. Past ReadyForQuery the pre-auth deadline is
    /// retired and reads may legitimately wait forever (an idle pooled connection), but a write
    /// that never completes means the peer stopped draining its receive window: a client that
    /// sends a query and never reads the result would otherwise hold its admission slot and the
    /// result buffers indefinitely. A stalled write ends the session with
    /// <see cref="PgWriteTimeoutException"/>, an <see cref="IOException"/> the connection handler
    /// already treats as a lifecycle fault, so the slot is released.
    ///
    /// <para>One owned <see cref="CancellationTokenSource"/> driven by the injected
    /// <see cref="TimeProvider"/>, linked once to the connection token and re-armed per write
    /// (<c>CancelAfter</c>), so a row-by-row result costs a timer change per write, not an
    /// allocation. Reads pass straight through with the caller's token.</para>
    /// </summary>
    internal sealed class PgWriteDeadlineStream : Stream
    {
        private readonly Stream _inner;
        private readonly TimeSpan _timeout;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationToken _connectionToken;

        public PgWriteDeadlineStream(Stream inner, TimeSpan timeout, TimeProvider timeProvider, CancellationToken connectionToken)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _timeout = timeout;
            _connectionToken = connectionToken;
            _deadline = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, _deadline.Token);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _deadline.CancelAfter(_timeout);
            try
            {
                await _inner.WriteAsync(buffer, _linked.Token);
            }
            catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !_connectionToken.IsCancellationRequested)
            {
                throw Expired();
            }
            Disarm();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _deadline.CancelAfter(_timeout);
            try
            {
                await _inner.FlushAsync(_linked.Token);
            }
            catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !_connectionToken.IsCancellationRequested)
            {
                throw Expired();
            }
            Disarm();
        }

        // A deadline that fires between the inner write completing and this call has already
        // cancelled the token; the next write's linked token is then cancelled and surfaces as
        // an expiry, which is correct — that peer did take longer than the timeout to drain.
        private void Disarm() => _deadline.CancelAfter(Timeout.InfiniteTimeSpan);

        private PgWriteTimeoutException Expired()
            => new($"pgwire peer did not drain a write within {_timeout}; closing the session.");

        public override int Read(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();
        public override void Write(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();
        public override void Flush() => throw SyncIoNotSupported();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private static NotSupportedException SyncIoNotSupported()
            => new("PgWriteDeadlineStream is async only; use ReadAsync, WriteAsync and FlushAsync.");

        protected override void Dispose(bool disposing)
        {
            // The inner stream belongs to the connection handler; only the timers are ours.
            if (disposing)
            {
                _linked.Dispose();
                _deadline.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>An authenticated session's peer stopped draining writes past <see cref="PgWireOptions.WriteTimeout"/>.</summary>
    internal sealed class PgWriteTimeoutException : IOException
    {
        public PgWriteTimeoutException(string message) : base(message) { }
    }
}

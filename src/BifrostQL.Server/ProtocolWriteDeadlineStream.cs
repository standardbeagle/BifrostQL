namespace BifrostQL.Server
{
    /// <summary>
    /// Bounds every WRITE a raw-wire session makes. A message loop's read deadline does nothing
    /// for a peer that sends requests and never reads the answers: the server's flush parks on
    /// transport backpressure (a full TCP window, a paused Kestrel output pipe) and holds the
    /// admission slot for as long as the peer likes. Two bounds close that:
    ///
    /// <list type="bullet">
    /// <item><b>The pre-auth deadline.</b> Every write is cancelled by
    /// <see cref="ProtocolPreAuthDeadline.Token"/>, so an unauthenticated session's writes end
    /// when its one cumulative pre-auth budget does — a pipelined PING flood into a zero window
    /// cannot outlive <c>AuthenticationTimeout</c> (invariant 15). A credentialed action retires
    /// the deadline and with it this bound; a return to anonymous re-arms both.</item>
    /// <item><b>The write timeout.</b> Each write and each flush must complete within
    /// <c>writeTimeout</c>, authenticated or not, so a stalled reader cannot hold an
    /// authenticated session open past it either. It is a stall bound, not a reply budget: a
    /// reply that keeps draining keeps going.</item>
    /// </list>
    ///
    /// <para>A front door with no pre-auth phase (RESP with <c>RequireAuthentication = false</c>)
    /// passes no deadline: its sessions never authenticate, so a deadline-bound write would drop
    /// every connection one pre-auth timeout after connect (H11). The write timeout still applies.</para>
    ///
    /// <para>Expiry surfaces as <see cref="OperationCanceledException"/>, which every connection
    /// loop already treats as end-of-connection, so the session returns and the host releases the
    /// slot. Reads pass straight through: the loop bounds those itself.</para>
    /// </summary>
    internal sealed class ProtocolWriteDeadlineStream : Stream
    {
        private readonly Stream _inner;
        private readonly ProtocolPreAuthDeadline? _deadline;
        private readonly TimeSpan _writeTimeout;
        // One timer for the session's life, re-armed per write and stood down after it. A session
        // writes one operation at a time, so the timer is never shared between two writes.
        private readonly CancellationTokenSource _writeTimer;

        public ProtocolWriteDeadlineStream(
            Stream inner, ProtocolPreAuthDeadline? deadline, TimeSpan writeTimeout, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(timeProvider);
            if (writeTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(writeTimeout), writeTimeout, "write timeout must be positive and finite.");
            _inner = inner;
            _deadline = deadline;
            _writeTimeout = writeTimeout;
            _writeTimer = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        }

        /// <summary>The wrapped transport (TLS detection, diagnostics).</summary>
        public Stream Inner => _inner;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var bound = Arm(cancellationToken);
            try
            {
                await _inner.WriteAsync(buffer, bound.Token).ConfigureAwait(false);
            }
            finally
            {
                StandDown();
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            using var bound = Arm(cancellationToken);
            try
            {
                await _inner.FlushAsync(bound.Token).ConfigureAwait(false);
            }
            finally
            {
                StandDown();
            }
        }

        private CancellationTokenSource Arm(CancellationToken caller)
        {
            _writeTimer.CancelAfter(_writeTimeout);
            return _deadline is null
                ? CancellationTokenSource.CreateLinkedTokenSource(caller, _writeTimer.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token, _writeTimer.Token);
        }

        private void StandDown()
        {
            if (!_writeTimer.IsCancellationRequested)
                _writeTimer.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // Async only: a synchronous write would block a pool thread on the very backpressure this
        // type exists to bound, with no token to cancel it.
        public override int Read(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();
        public override void Write(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();
        public override void Flush() => throw SyncIoNotSupported();

        private static NotSupportedException SyncIoNotSupported()
            => new("ProtocolWriteDeadlineStream is async only; use ReadAsync, WriteAsync and FlushAsync.");

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The transport belongs to the connection handler that created it; only the timer is ours.
            if (disposing)
                _writeTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}

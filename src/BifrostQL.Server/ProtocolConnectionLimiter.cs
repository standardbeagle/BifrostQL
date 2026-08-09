namespace BifrostQL.Server
{
    /// <summary>
    /// Lock-free admission counter capping the concurrent connections of one protocol-adapter
    /// listener. A single shared instance is consulted by every connection of that front door:
    /// <see cref="TryAcquire"/> reserves a slot with an optimistic compare-and-swap (no lock in the
    /// accept hot path), and <see cref="Release"/> — always called from the connection's
    /// <c>finally</c> — returns it. Over the limit, admission fails cleanly (the caller answers its
    /// protocol's "too many connections" error and closes) rather than blocking or crashing.
    ///
    /// <para>Adapters MUST acquire the slot at ACCEPT, before any read, TLS handshake or
    /// authentication work. A cap applied later bounds admitted SESSIONS but not the work an
    /// unauthenticated peer can force, which is not a cap on the resource. Pair it with a
    /// pre-auth deadline: reserving the slot early makes a silent peer's stall more expensive,
    /// not less.</para>
    ///
    /// <para>One implementation, one set of semantics, shared by every adapter. Each front door
    /// derives its OWN type (see <c>PgwireConnectionLimiter</c>, <c>RespConnectionLimiter</c>) so
    /// the container hands each listener a DISTINCT instance: registering the base type from two
    /// adapters would silently give them one shared counter, and a host running both would find
    /// pgwire connections consuming RESP's budget.</para>
    /// </summary>
    internal abstract class ProtocolConnectionLimiter
    {
        private readonly int _max;
        private int _current;

        protected ProtocolConnectionLimiter(int maxConnections)
        {
            if (maxConnections < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConnections),
                    "A protocol listener's MaxConnections must be at least 1.");
            _max = maxConnections;
        }

        /// <summary>Current number of admitted connections (for diagnostics/tests).</summary>
        public int Count => Volatile.Read(ref _current);

        /// <summary>
        /// Optimistically reserves one connection slot. Returns false when the limit is
        /// already reached, without mutating the counter. Lock-free CAS loop.
        /// </summary>
        public bool TryAcquire()
        {
            while (true)
            {
                var observed = Volatile.Read(ref _current);
                if (observed >= _max) return false;
                if (Interlocked.CompareExchange(ref _current, observed + 1, observed) == observed)
                    return true;
                // Lost the race to another connection; re-observe and retry.
            }
        }

        /// <summary>Returns a previously acquired slot. Idempotency is the caller's contract.</summary>
        public void Release() => Interlocked.Decrement(ref _current);
    }

    /// <summary>Admission counter for the pgwire listener (<c>PgWireOptions.MaxConnections</c>).</summary>
    internal sealed class PgwireConnectionLimiter : ProtocolConnectionLimiter
    {
        public PgwireConnectionLimiter(int maxConnections) : base(maxConnections) { }
    }

    /// <summary>Admission counter for the RESP listener (<c>RespWireOptions.MaxConnections</c>).</summary>
    internal sealed class RespConnectionLimiter : ProtocolConnectionLimiter
    {
        public RespConnectionLimiter(int maxConnections) : base(maxConnections) { }
    }
}

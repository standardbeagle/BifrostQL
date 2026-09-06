namespace BifrostQL.Server
{
    /// <summary>
    /// Lock-free admission counter capping a protocol front door's countable resource — normally
    /// its concurrent connections, and for LDAP also the in-flight operations of one connection.
    /// A single shared instance is consulted by every caller contending for that resource:
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

        protected ProtocolConnectionLimiter(int max)
        {
            if (max < 1)
                throw new ArgumentOutOfRangeException(nameof(max),
                    "A protocol listener's admission cap must be at least 1.");
            _max = max;
        }

        /// <summary>Current number of admitted connections (for diagnostics/tests).</summary>
        public int Count => Volatile.Read(ref _current);

        /// <summary>The configured cap (for the refusal message this listener answers with).</summary>
        public int Max => _max;

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

    /// <summary>
    /// Admission counter for the LDAP front door (<c>LdapWireOptions.MaxConnections</c>). ONE
    /// instance serves both the cleartext listener and the LDAPS listener, because MaxConnections
    /// bounds this front door's total concurrent connections — opening a second port must not
    /// double the ceiling.
    /// </summary>
    internal sealed class LdapConnectionLimiter : ProtocolConnectionLimiter
    {
        public LdapConnectionLimiter(int maxConnections) : base(maxConnections) { }
    }

    /// <summary>
    /// Per-connection cap on an LDAP session's simultaneously-outstanding operations
    /// (<c>LdapWireOptions.MaxOutstandingOperations</c>) — the same admission mechanism as the
    /// connection counter, one scope down: a fresh instance is created for each admitted session,
    /// so one peer's pipelining cannot consume another session's budget.
    /// </summary>
    internal sealed class LdapOutstandingOperationLimiter : ProtocolConnectionLimiter
    {
        public LdapOutstandingOperationLimiter(int maxOutstandingOperations) : base(maxOutstandingOperations) { }
    }

    /// <summary>Admission counter for the RESP listener (<c>RespWireOptions.MaxConnections</c>).</summary>
    internal sealed class RespConnectionLimiter : ProtocolConnectionLimiter
    {
        public RespConnectionLimiter(int maxConnections) : base(maxConnections) { }
    }

    /// <summary>
    /// Admission counter for the gRPC listener (<c>GrpcWireOptions.MaxConcurrentConnections</c>).
    /// Enforced by per-listener Kestrel connection middleware (the slot is reserved at ACCEPT,
    /// before any HTTP/2 frame, TLS handshake included) — NOT via
    /// <c>KestrelServerOptions.Limits.MaxConcurrentConnections</c>, which is process-global
    /// state that would silently throttle the host's own HTTP listeners too.
    /// </summary>
    internal sealed class GrpcConnectionLimiter : ProtocolConnectionLimiter
    {
        public GrpcConnectionLimiter(int maxConnections) : base(maxConnections) { }
    }

    /// <summary>
    /// Admission counter for one binary WebSocket mount
    /// (<c>UseBifrostBinary(maxConnections:)</c>). Unlike the Kestrel-hosted listeners this
    /// front door is HTTP middleware, so its "accept" is the WebSocket upgrade: the slot is
    /// reserved before <c>AcceptWebSocketAsync</c> and before the mount's identity gate, and
    /// released when the connection handler returns. The instance is owned by the middleware
    /// instance, which ASP.NET Core creates once per mount, so two binary mounts on one host
    /// never share a budget.
    /// </summary>
    internal sealed class BinaryTransportConnectionLimiter : ProtocolConnectionLimiter
    {
        public BinaryTransportConnectionLimiter(int maxConnections) : base(maxConnections) { }
    }
}

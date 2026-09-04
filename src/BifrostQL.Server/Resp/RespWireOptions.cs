using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Configuration for the Redis RESP-protocol front door (slice 1: codec, connection
    /// loop, PING/HELLO/AUTH/SELECT/INFO plumbing, fail-closed identity). The data command
    /// surface it fronts (GET/SET/HGETALL/SCAN…) attaches at the dispatch seam in later
    /// slices; the query surface those commands read is selected by <see cref="Endpoint"/>.
    /// </summary>
    public sealed class RespWireOptions
    {
        /// <summary>TCP port the front door listens on. Default 6379 (the Redis port).</summary>
        public int Port { get; set; } = 6379;

        /// <summary>
        /// The IP address this listener binds to. <b>Defaults to loopback (127.0.0.1)</b>: the
        /// adapter is reachable only from the host until an operator deliberately widens it.
        ///
        /// <para>This is a DEFAULT CHANGE. The listener previously bound <c>ListenAnyIP</c>
        /// (0.0.0.0) with no way to narrow it, so merely registering the adapter exposed a
        /// database front door to every network the host sits on — an ambient decision nobody
        /// made. Per the project's exposure-posture rule, an undeclared posture IS loopback, and
        /// widening it (loopback -> LAN -> public) is an operator decision, not an ambient
        /// default. Set <c>IPAddress.Any</c> (or a specific interface) to opt in, which now
        /// appears explicitly in the host's own startup code where it can be reviewed.</para>
        /// </summary>
        public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

        /// <summary>
        /// When <c>true</c> (the default), a connection must complete <c>AUTH</c> (or inline
        /// <c>HELLO … AUTH</c>) before any command that needs an identity runs; until then
        /// those commands are refused with <c>NOAUTH</c>. There is no anonymous mode unless a
        /// deployment explicitly sets this <c>false</c> — the front door never establishes a
        /// session with a subject-less/anonymous identity while authentication is required.
        /// </summary>
        public bool RequireAuthentication { get; set; } = true;

        /// <summary>
        /// Hard cap on the byte length of any single bulk/verbatim string and on the length
        /// of any inline line, applied on the UNAUTHENTICATED path (DoS guard). A hostile
        /// length prefix beyond this is refused with a protocol error, never allocated —
        /// mirrors the pgwire <c>MaxMessageLength</c> guard. Default 1 MiB; per-command
        /// larger limits for data writes arrive with the data slices.
        /// </summary>
        public int MaxBulkLength { get; set; } = 1 << 20;

        /// <summary>
        /// Hard cap on the TOTAL byte length of one top-level frame — every byte the decoder
        /// consumes between the frame's first marker and its last terminator, elements of a nested
        /// aggregate included. Applied on the UNAUTHENTICATED path (DoS guard).
        ///
        /// <para>The per-element caps do not bound a frame: <see cref="MaxBulkLength"/> bounds ONE
        /// bulk string and <see cref="MaxAggregateElements"/> bounds ONE aggregate's element count,
        /// so their product — about 1 TiB at the defaults — is what a single frame could reach, and
        /// every decoded element is retained until the frame completes. The budget is decremented
        /// per consumed byte and checked BEFORE any payload is allocated, so an oversized frame is
        /// refused with a clean protocol error having materialized nothing. It is reset at each
        /// top-level frame, never by an inner element (that would defeat the cap). Default 1 MiB,
        /// matching the pgwire and LDAP per-message caps.</para>
        /// </summary>
        public int MaxFrameLength { get; set; } = 1 << 20;

        /// <summary>
        /// Hard cap on the declared element count of any array/set/push/map, applied on the
        /// UNAUTHENTICATED path (DoS guard) so a huge multibulk count cannot pre-allocate an
        /// unbounded array. Default 1,048,576.
        /// </summary>
        public int MaxAggregateElements { get; set; } = 1 << 20;

        /// <summary>
        /// Hard cap on how deeply aggregates (array/set/push/map) may nest, applied on the
        /// UNAUTHENTICATED path (DoS guard). The recursive decoder consumes one physical stack
        /// frame per nesting level, and because buffered socket bytes let the reads complete
        /// synchronously, an unauthenticated peer sending a few KB of repeated aggregate headers
        /// (e.g. <c>*1\r\n</c>×N) would otherwise grow the stack without bound until an
        /// uncatchable <c>StackOverflowException</c> tears down the whole host process. The
        /// decoder refuses to descend past this cap, raising a clean protocol error the
        /// connection loop handles instead. Default 32: real RESP3 traffic (push → array of
        /// maps, HELLO reply maps, nested command arrays) nests only a handful deep, so 32 is
        /// generous headroom while a chain that deep is unambiguously hostile.
        /// </summary>
        public int MaxNestingDepth { get; set; } = 32;

        /// <summary>
        /// Maximum number of concurrent connections the front door admits. The slot is reserved at
        /// ACCEPT — before the codec reads a byte and before AUTH — so an unauthenticated peer can
        /// never force work outside the cap; the N+1th connection is refused with a clean
        /// <c>-ERR</c> and closed. Without this, an uncapped listener let any peer exhaust sockets,
        /// threads and memory with no credentials at all. Default 100, matching pgwire.
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Deadline for a connection to complete AUTH. An unauthenticated peer that opens a socket
        /// and then stalls holds its admission slot indefinitely, which — now that the slot is taken
        /// at accept — is a complete denial of service needing no credentials and no bytes. Expiry
        /// closes the connection and releases the slot. Does NOT apply once authenticated: an idle
        /// AUTHENTICATED connection is a normal pooled client, and Redis clients pool aggressively.
        /// Default 30 seconds, matching pgwire's handshake deadline.
        /// </summary>
        public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum authentication attempts admitted from one source within
        /// <see cref="AuthRateLimitWindow"/>. Redis keeps a connection usable after a failed AUTH so
        /// a client can retry, which left password guessing unbounded: one socket could try forever
        /// and reconnecting cost nothing. The source is the peer's address where the listener knows
        /// it, and the connection itself otherwise — never a shared bucket, which one hostile peer
        /// could use to lock everyone else out. Default 100, matching the LDAP bind limiter.
        /// </summary>
        public int MaxAuthAttemptsPerSource { get; set; } = 100;

        /// <summary>
        /// Maximum authentication attempts admitted against one account name within
        /// <see cref="AuthRateLimitWindow"/>, whatever their source. Bounds a distributed guess at
        /// one login, which the per-source cap alone cannot see. Default 10, matching LDAP.
        /// </summary>
        public int MaxAuthAttemptsPerAccount { get; set; } = 10;

        /// <summary>The fixed window both authentication-attempt caps are counted over. Default 1 minute.</summary>
        public TimeSpan AuthRateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Maximum time an AUTHENTICATED connection may sit with no command before it is closed.
        /// Bounds the resource an abandoned-but-open client holds; the client simply reconnects.
        /// Default 10 minutes — far above any real client's keepalive interval, so it reaps leaks
        /// rather than disturbing pooled connections. Set to <see cref="Timeout.InfiniteTimeSpan"/>
        /// to disable (deliberately possible, deliberately not the default).
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Secret the opaque SCAN cursor is MAC'd with. Configure it to make cursors survive a
        /// restart and resolve across instances; absent one a per-instance random key is generated
        /// and the trade-off is logged at startup. The cursor carries only a primary-key position —
        /// the MAC is a tamper and replay guard, not the authorization boundary, which the query
        /// pipeline holds unconditionally.
        /// </summary>
        public string? ScanCursorSecret { get; set; }

        /// <summary>
        /// How long an issued SCAN cursor stays valid. An expired cursor is refused exactly like a
        /// forged one — the same outcome, so neither is distinguishable from the other. Default 10
        /// minutes, comfortably above any real iteration.
        /// </summary>
        public TimeSpan ScanCursorTtl { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Registered BifrostQL endpoint path (e.g. <c>/graphql</c>) whose model, schema and
        /// connection authenticated sessions execute their data commands against. Null selects
        /// the single registered endpoint. Unused by slice-1 plumbing; carried for the data
        /// slices.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Master gate for the RESP WRITE surface (SET/HSET/DEL). <b>Off by default</b>: until a
        /// deployment explicitly opts in by setting this <c>true</c>, every write command is
        /// refused with a clean <c>-ERR</c> and executes NOTHING — the front door exposes reads
        /// only. This is a fail-closed posture: a write path is the highest-risk surface, so it
        /// stays dark unless deliberately turned on. When enabled, writes route through
        /// <c>IMutationIntentExecutor</c> under the session identity, so the full mutation
        /// transformer chain (tenant scoping, audit actor, soft-delete, field-encryption-on-write,
        /// CDC/history hooks) applies and is unskippable. Enabling it is logged at startup as a
        /// notable posture change.
        /// </summary>
        public bool EnableWrites { get; set; }

        /// <summary>
        /// The TLS certificate the listener presents. When set, Kestrel performs the TLS
        /// handshake on this listener BEFORE the connection handler sees any byte, and every
        /// connection is confidential: <c>AUTH</c> / <c>HELLO … AUTH</c> may read and compare
        /// the password. Null (the default) keeps the listener plain TCP.
        ///
        /// <para>This is the one in-code way to make the AUTH path confidential — RESP has no
        /// STARTTLS. Without it, every credential-bearing command is refused at the transport
        /// gate (a uniform, transport-only refusal that never varies by account) unless
        /// <see cref="AllowCleartextAuth"/> is explicitly set.</para>
        /// </summary>
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>
        /// Development-only override: accept <c>AUTH</c> credentials over a cleartext transport.
        /// <b>Off by default</b> — the credential-bearing handshake must never read, resolve or
        /// compare a password over plain TCP, so the transport gate refuses it (uniformly, never
        /// varying by account) unless TLS is configured via <see cref="ServerCertificate"/> or
        /// this override is explicitly set. Enabling it is logged as a warning at startup and is
        /// meant for loopback development behind a TLS-terminating proxy; it must be off for any
        /// real deployment. Never inferred from a missing certificate.
        /// </summary>
        public bool AllowCleartextAuth { get; set; }
    }
}

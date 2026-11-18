using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Configuration for the PostgreSQL wire-protocol front door (slice 1: startup,
    /// TLS, authentication). The query surface it fronts is selected by
    /// <see cref="Endpoint"/> (a registered BifrostQL endpoint path); the query loop
    /// itself arrives in a later slice.
    /// </summary>
    /// <summary>The authentication challenge the front door issues after startup.</summary>
    public enum PgAuthMethod
    {
        /// <summary>
        /// SCRAM-SHA-256 (RFC 7677): the secret never crosses the wire. The secure
        /// default; use it whenever the credential store holds a shared secret.
        /// </summary>
        ScramSha256,

        /// <summary>
        /// AuthenticationCleartextPassword: the secret crosses the (TLS-wrapped) wire.
        /// For credential sources that cannot participate in SCRAM — e.g. an OIDC
        /// client-secret exchanged server-side for a token.
        /// </summary>
        Cleartext,
    }

    public sealed class PgWireOptions
    {
        /// <summary>TCP port the front door listens on. Default 5432 (the PostgreSQL port).</summary>
        public int Port { get; set; } = 5432;

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

        /// <summary>Which authentication challenge to issue. SCRAM-SHA-256 by default.</summary>
        public PgAuthMethod AuthMethod { get; set; } = PgAuthMethod.ScramSha256;

        /// <summary>
        /// Development-only override that permits the <see cref="PgAuthMethod.Cleartext"/>
        /// password exchange over a NON-TLS connection. Default OFF: a cleartext password
        /// is only ever invited and read once the session is confidential (an SslStream);
        /// a client that skips SSLRequest is refused BEFORE the password challenge, with a
        /// transport-only message that does not vary by account existence. Enabling this
        /// sends passwords in the clear and logs a startup warning. SCRAM is unaffected —
        /// it never puts the password on the wire. Ignored unless AuthMethod is Cleartext.
        /// </summary>
        public bool AllowCleartextPasswordWithoutTls { get; set; }

        /// <summary>
        /// Maximum number of concurrent authenticated + admitted connections. The N+1th
        /// connection is refused cleanly with <c>53300 too_many_connections</c> during
        /// startup and closed — never left to crash or hang. Enforced lock-free by
        /// <see cref="PgwireConnectionLimiter"/>. Default 100.
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Maximum number of live NAMED prepared statements a single session may hold. Every
        /// named statement a client Parses is retained for the life of the connection, so
        /// without a cap ONE peer grows server memory without bound by Parsing fresh names.
        /// Exceeding it is a clean <c>53400 configuration_limit_exceeded</c> on the offending
        /// Parse (skip-until-Sync; the session survives and its existing statements still work),
        /// never a silent eviction of a statement the client still holds a name for. The unnamed
        /// statement is exempt — it always REPLACES, so it cannot accumulate. Default 200: real
        /// drivers cache a few dozen statements per connection at most.
        /// </summary>
        public int MaxPreparedStatements { get; set; } = 200;

        /// <summary>
        /// Maximum number of live NAMED portals a single session may hold, capped for the same
        /// reason as <see cref="MaxPreparedStatements"/> — and more sharply, because a portal
        /// also caches its MATERIALIZED result rows for row-limited Execute resume, so an
        /// uncapped portal map is bounded by result-set size, not by statement text. The unnamed
        /// portal is exempt (it always replaces). Default 200.
        /// </summary>
        public int MaxPortals { get; set; } = 200;

        /// <summary>
        /// Total UTF-8 bytes of SQL text the NAMED prepared statements of one session may hold.
        /// <see cref="MaxPreparedStatements"/> counts statements, not bytes, so on its own it let
        /// one connection retain 200 frames of SQL (~200 MiB). A Parse that would cross the budget
        /// is refused with <c>53400</c> and the session survives. The unnamed statement always
        /// replaces and is exempt. Default 8 MiB.
        /// </summary>
        public long MaxSessionStatementBytes { get; set; } = 8L * 1024 * 1024;

        /// <summary>
        /// Total bytes of encoded result rows the suspended portals of one session may retain for
        /// row-limited Execute resume. Portals close at Sync (end of the implicit transaction), so
        /// this bounds what one Sync batch can pin; an Execute whose suspension would cross it is
        /// refused with <c>53400</c> before any row is sent. Default 32 MiB.
        /// </summary>
        public long MaxSessionPortalBytes { get; set; } = 32L * 1024 * 1024;

        /// <summary>
        /// Deadline for a connection to get from accept to an authenticated, ready session.
        /// Applied to every read of the PRE-AUTH phase (SSLRequest/TLS handshake, StartupMessage,
        /// the password/SCRAM exchange), so an unauthenticated peer that opens a socket and then
        /// stalls — the cheapest possible slot-exhaustion attack, since the slot is reserved at
        /// accept — is dropped instead of holding its slot forever. Default 30 seconds.
        /// </summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Deadline for each write of an AUTHENTICATED session to drain to the peer. Reads after
        /// ReadyForQuery have no deadline (an idle pooled connection is normal), but a peer that
        /// sends a query and then stops reading leaves the server's write blocked on a full
        /// receive window; without this bound it held its connection slot and result buffers
        /// forever. A write that does not complete in time closes the session and releases the
        /// slot. Must be positive. Default 60 seconds.
        /// </summary>
        public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Maximum authentication attempts (SCRAM or cleartext) admitted per connection source
        /// (client IP) within <see cref="AuthRateLimitWindow"/> before further attempts from
        /// that source are refused. Both methods verify through PBKDF2(4096), so an unbounded
        /// attempt rate is a CPU-exhaustion vector; the refusal happens BEFORE any credential
        /// lookup or hash work and answers with the SAME invalid_password wire shape as a
        /// wrong password (no throttle-state oracle). Enforced by <see cref="PgAuthRateLimiter"/>.
        /// Default 100.
        /// </summary>
        public int MaxAuthAttemptsPerSource { get; set; } = 100;

        /// <summary>
        /// Maximum authentication attempts admitted against one login name within
        /// <see cref="AuthRateLimitWindow"/>, whatever their source. Each connection carries one
        /// attempt, but reconnecting is free, so without this axis many source addresses could
        /// guess one account without bound. Every connection counts, successful or not, so size
        /// it above the rate at which a connection pool for one login reconnects. Default 100.
        /// </summary>
        public int MaxAuthAttemptsPerAccount { get; set; } = 100;

        /// <summary>The fixed window over which both auth caps are counted. Default 1 minute.</summary>
        public TimeSpan AuthRateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The server certificate presented when a client issues SSLRequest. Required:
        /// the front door refuses to start without it rather than silently answering 'N'
        /// (no-TLS) to every client — credentials must never cross the wire in the clear
        /// by misconfiguration. TLS negotiation itself remains client-initiated per the
        /// protocol (a client that never sends SSLRequest is answered on the raw socket).
        /// </summary>
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>
        /// Registered BifrostQL endpoint path (e.g. <c>/graphql</c>) whose model, schema
        /// and connection authenticated sessions execute against. Null selects the single
        /// registered endpoint.
        /// </summary>
        public string? Endpoint { get; set; }
    }
}

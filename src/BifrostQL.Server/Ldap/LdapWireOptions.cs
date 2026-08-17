using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Configuration for the LDAPv3 protocol front door (slice 2: BER codec + bounded connection
    /// lifecycle). The front door is opt-in — it exists only when a host calls
    /// <c>AddBifrostLdap</c>. Bind authentication, search execution, TLS, SASL, and writes are
    /// non-goals of this slice: the loop decodes every request and answers the ones it does not yet
    /// execute with <c>unwillingToPerform</c>, never hanging.
    ///
    /// <para>Every limit here is a pre-auth DoS guard on an unauthenticated wire; the defaults are
    /// generous for real LDAP traffic yet reject the pathological inputs those guards exist for.</para>
    /// </summary>
    public sealed class LdapWireOptions
    {
        /// <summary>TCP port the front door listens on. Default 389 (the LDAP port).</summary>
        public int Port { get; set; } = 389;

        /// <summary>
        /// Address the front door binds. Default <see cref="IPAddress.Loopback"/>: an undeclared
        /// exposure posture IS loopback, so merely registering this database front door never
        /// publishes it to every network the host sits on. Widening it (loopback -> LAN -> public) is
        /// an operator decision that has to be written down here. See AGENTS.md "Listener exposure
        /// posture".
        /// </summary>
        public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

        /// <summary>
        /// Hard cap on the byte length of a single LDAPMessage, applied on the UNAUTHENTICATED path.
        /// A definite-length prefix beyond this is refused with a protocol error BEFORE the body
        /// buffer is allocated, so a hostile length prefix cannot force a giant allocation. Default 1 MiB.
        /// </summary>
        public int MaxMessageLength { get; set; } = 1 << 20;

        /// <summary>
        /// Hard cap on how deeply a search filter may nest (<c>and</c>/<c>or</c>/<c>not</c>). The
        /// filter decoder consumes one physical stack frame per level and, because the whole message
        /// is already buffered, the recursion is synchronous — an unguarded decoder would grow the
        /// stack without bound on a deeply-nested filter until an uncatchable
        /// <c>StackOverflowException</c> tore down the whole host process. The decoder refuses to
        /// descend past this cap, raising a clean protocol error instead. Default 32: real filters
        /// nest only a handful deep, so 32 is generous headroom while a chain that deep is hostile.
        /// </summary>
        public int MaxNestingDepth { get; set; } = 32;

        /// <summary>
        /// Hard cap on the total number of filter nodes in one SearchRequest, bounding CPU / list
        /// growth within an already size-capped message. Default 1024.
        /// </summary>
        public int MaxFilterComponents { get; set; } = 1024;

        /// <summary>Hard cap on the number of attributes a single SearchRequest may request. Default 1024.</summary>
        public int MaxSearchAttributes { get; set; } = 1024;

        /// <summary>
        /// Maximum number of concurrent connections across the whole front door — BOTH the cleartext
        /// and LDAPS listeners, so opening the second port does not double the ceiling. The N+1th
        /// connection is refused cleanly and closed — never left to crash or hang — enforced
        /// lock-free by <see cref="LdapBoundedCounter"/>. Default 100, matching the pgwire and RESP
        /// listeners: these caps bound what an unauthenticated peer can make the host hold, so they
        /// are set per front door, not per protocol's taste.
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Maximum number of simultaneously-outstanding operations on a single connection, bounding a
        /// client that pipelines requests without waiting. Default 64.
        /// </summary>
        public int MaxOutstandingOperations { get; set; } = 64;

        /// <summary>
        /// How long a connection may sit idle (no complete message received) before it is closed.
        /// Bounds a peer that opens a socket and stalls, holding a connection slot indefinitely.
        /// Default 5 minutes.
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Registered BifrostQL endpoint path whose directory model the later search slice serves.
        /// Null selects the single registered endpoint. Carried but unused by this codec/lifecycle slice.
        /// </summary>
        public string? Endpoint { get; set; }

        // ---- bind authentication (slice 3) ----

        /// <summary>
        /// Whether an anonymous bind (zero-length DN + zero-length password) is admitted. Default
        /// FALSE (fail-closed): an anonymous bind is refused with invalidCredentials unless a
        /// deployment explicitly opts in. When enabled, an admitted anonymous session may read only the
        /// RootDSE / subschema until a later policy explicitly grants data search (criterion 4).
        /// </summary>
        public bool AnonymousBindEnabled { get; set; }

        /// <summary>
        /// Hard cap on the byte length of a presented simple-bind password. A password beyond this is
        /// refused BEFORE it reaches the adaptive password hasher, so an oversized password cannot be
        /// weaponized as a hash-DoS (criterion 3). Default 4096 bytes — far above any real password,
        /// yet a bound.
        /// </summary>
        public int MaxPasswordLength { get; set; } = 4096;

        /// <summary>
        /// Maximum bind attempts admitted per connection source within
        /// <see cref="BindRateLimitWindow"/> before further attempts from that source are refused.
        /// Bounds one client spraying credentials across many accounts. Default 100.
        /// </summary>
        public int MaxBindAttemptsPerSource { get; set; } = 100;

        /// <summary>
        /// Maximum bind attempts admitted per bind DN within <see cref="BindRateLimitWindow"/> before
        /// further attempts against that account are refused. Bounds a brute-force against one account.
        /// Default 10.
        /// </summary>
        public int MaxBindAttemptsPerAccount { get; set; } = 10;

        /// <summary>The fixed window over which the per-source and per-account bind caps are counted. Default 1 minute.</summary>
        public TimeSpan BindRateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Pre-auth deadline: how long a connection may stay UNAUTHENTICATED before it is closed. The
        /// admission slot is taken at accept, so a peer that never authenticates would otherwise hold
        /// it for the whole <see cref="IdleTimeout"/> window while failing binds keep the connection
        /// non-idle — an unauthenticated slot-exhaustion vector the idle timeout does not cover. Once a
        /// bind succeeds the session is bounded by <see cref="IdleTimeout"/> instead. Default 30
        /// seconds, matching the pgwire handshake and RESP authentication deadlines.
        /// </summary>
        public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // ---- transport security ----

        /// <summary>
        /// DEVELOPMENT ONLY. When true, a credentialed simple bind is admitted on a CLEARTEXT
        /// connection — the presented password crosses the wire in the clear, readable by anything on
        /// the path. Default FALSE: without LDAPS or a completed StartTLS, a credentialed bind is
        /// refused with <c>confidentialityRequired</c> before the credential is read.
        ///
        /// <para>Enabling it is a deliberate posture change and is announced with a startup WARNING;
        /// it is never inferred from a missing certificate, because a silent cleartext fallback is
        /// exactly the downgrade the gate exists to prevent.</para>
        /// </summary>
        public bool AllowInsecureSimpleBind { get; set; }

        /// <summary>
        /// The server certificate presented on the LDAPS listener and by a StartTLS upgrade. One
        /// certificate serves both surfaces so they cannot diverge. Takes precedence over
        /// <see cref="TlsCertificatePath"/>; leaving both unset means a cleartext-only front door,
        /// where StartTLS is answered <c>unavailable</c> and every credentialed bind is refused.
        /// </summary>
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>
        /// Path to a PKCS#12 (.pfx) file holding the server certificate AND its private key, used
        /// when <see cref="ServerCertificate"/> is not supplied. A path that cannot be loaded aborts
        /// startup — it is never treated as "no certificate configured", because that would silently
        /// downgrade a listener the operator meant to be confidential.
        /// </summary>
        public string? TlsCertificatePath { get; set; }

        /// <summary>Password for <see cref="TlsCertificatePath"/>, when the file is protected.</summary>
        public string? TlsCertificatePassword { get; set; }

        /// <summary>
        /// TCP port for the implicit-TLS (LDAPS) listener, conventionally 636. Null (the default)
        /// means no LDAPS listener: the front door serves the cleartext port only, where StartTLS is
        /// the route to confidentiality. Setting it REQUIRES a configured certificate — an LDAPS port
        /// with nothing to present is a startup failure, not a cleartext port. It binds the same
        /// <see cref="BindAddress"/> as the cleartext listener, so both share one declared posture.
        /// </summary>
        public int? LdapsPort { get; set; }

        /// <summary>
        /// Deadline for a TLS handshake to complete, on the LDAPS listener and on a StartTLS upgrade.
        /// The admission slot is already held when the handshake starts, so a peer that stalls
        /// mid-handshake would otherwise hold it for the whole idle window. Default 30 seconds,
        /// matching <see cref="AuthenticationTimeout"/>.
        /// </summary>
        public TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}

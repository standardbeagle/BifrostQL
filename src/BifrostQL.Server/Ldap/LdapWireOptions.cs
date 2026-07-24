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
        /// Maximum number of concurrent connections across the whole front door. The N+1th connection
        /// is refused cleanly and closed — never left to crash or hang — enforced lock-free by
        /// <see cref="LdapBoundedCounter"/>. Default 1000.
        /// </summary>
        public int MaxConnections { get; set; } = 1000;

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
    }
}

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Configuration for the opt-in OData v4 HTTP endpoint. Disabled by default; a host enables
    /// it explicitly via <see cref="BifrostSetupOptions.AddODataEndpoint"/> or
    /// <see cref="BifrostMultiDbOptions.AddODataEndpoint"/>, mirroring the opt-in posture of the
    /// other protocol adapters (S3, pgwire, RESP). Enabling it does not alter the existing
    /// GraphQL/binary routes — the adapter is mounted on its own branch at <see cref="RoutePrefix"/>.
    /// </summary>
    public sealed class ODataOptions
    {
        /// <summary>Whether the OData endpoint is enabled. Default: false (opt-in).</summary>
        public bool Enabled { get; set; }

        /// <summary>Path prefix the endpoint listens under. Default: "/odata".</summary>
        public string RoutePrefix { get; set; } = "/odata";

        /// <summary>
        /// The realm advertised in the <c>WWW-Authenticate</c> challenge emitted on a 401. Only
        /// a label for interactive Basic clients; carries no security weight.
        /// </summary>
        public string Realm { get; set; } = "BifrostQL";

        /// <summary>
        /// The registered GraphQL endpoint whose cached model/connection future read slices
        /// resolve against. Null selects the single registered endpoint. Unused in slice 1
        /// (no reads yet); carried so the read seam lands without a config change.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// The page size applied to an entity-set read when the request supplies no <c>$top</c>.
        /// Every collection response is bounded so a caller can never pull an unbounded result set
        /// with a single request. Default: 100.
        /// </summary>
        public int DefaultPageSize { get; set; } = 100;

        /// <summary>
        /// The upper bound a requested <c>$top</c> is clamped to. A caller asking for more rows than
        /// this receives at most this many — the server-side ceiling on a single read. Default: 1000.
        /// </summary>
        public int MaxPageSize { get; set; } = 1000;

        /// <summary>
        /// The HMAC key the opaque server-driven-paging continuation token (<c>$skiptoken</c>) is
        /// signed with. A configured secret keeps continuation tokens valid across restarts and
        /// across a horizontally-scaled fleet; without one a per-instance random key is generated
        /// (a startup warning is logged) — tokens then resolve only within this process's lifetime.
        /// The token carries position only; the key is the tamper/replay guard, never the
        /// authorization boundary (tenant/policy scope is re-applied per request by the pipeline).
        /// </summary>
        public string? ContinuationTokenSecret { get; set; }

        /// <summary>
        /// How long a continuation token stays valid after it is minted. An expired token fails
        /// closed as a clean OData 400, exactly like a tampered one, so a stale nextLink can never
        /// silently serve a wrong page. Default: 15 minutes.
        /// </summary>
        public TimeSpan ContinuationTokenTtl { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The upper bound on the number of related rows a single <c>$expand</c> navigation may
        /// return across the whole page. A request whose expansion would exceed this fails closed as
        /// a clean OData 400 rather than materializing an unbounded fan-out — the row-count ceiling
        /// on one level of expansion (.claude/rules/protocol-adapter-security.md invariant 6).
        /// Default: 1000.
        /// </summary>
        public int MaxExpandFanout { get; set; } = 1000;
    }
}

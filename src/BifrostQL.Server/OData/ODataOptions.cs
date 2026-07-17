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
    }
}

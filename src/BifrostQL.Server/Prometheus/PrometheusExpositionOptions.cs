using System;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Deployment bounds for the Prometheus <c>/metrics</c> exposition endpoint — the knobs that
    /// keep a scrape storm from hammering the database (protocol-adapter-security invariant 6).
    /// Distinct from <see cref="PrometheusScrapeSecurityOptions"/> (the credential gate + scoping
    /// authority) and <see cref="PrometheusCollectionOptions"/> (the per-query timeout).
    /// </summary>
    public sealed class PrometheusExpositionOptions
    {
        /// <summary>The route the endpoint is mounted at. Prometheus convention is <c>/metrics</c>.</summary>
        public string RoutePath { get; init; } = "/metrics";

        /// <summary>
        /// The GraphQL endpoint path whose cached model backs the exported metrics. The exposition
        /// service resolves this model through <see cref="Core.Resolvers.IQueryIntentExecutor"/>.
        /// </summary>
        public string Endpoint { get; init; } = "/graphql";

        /// <summary>
        /// How long a successfully collected series stays fresh in the single-flight cache. Within
        /// the TTL, concurrent scrapes are served the cached series and collapse to ONE aggregate
        /// query per series (request coalescing) — a scrape storm cannot fan out to N queries.
        /// </summary>
        public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The global backstop on the number of label-value series a single metric may emit, applied
        /// when the metric declares no per-metric <c>metric-max-cardinality</c>. Null disables the
        /// global backstop, which makes a labeled metric with no per-metric cap an UNBOUNDED
        /// configuration — rejected fail-fast (an operator-visible error self-metric, never an
        /// unbounded series set on the wire).
        /// </summary>
        public int? GlobalMaxCardinality { get; init; } = 1000;

        /// <summary>
        /// Maximum request body the endpoint accepts. A Prometheus scrape is a body-less GET, so the
        /// default is 0 — any body over this cap is rejected (413) before collection, bounding an
        /// abusive-body request.
        /// </summary>
        public long MaxRequestBodyBytes { get; init; } = 0;
    }
}

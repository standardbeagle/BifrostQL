using System.Collections.Generic;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// The resolved scrape scope for one Prometheus metric: either the user context its aggregate
    /// runs under (<see cref="Included"/>), or an exclusion carrying a server-side-only
    /// <see cref="Reason"/> (a misconfiguration fails CLOSED to no metric, never to unscoped/global
    /// data). The reason is for server-side logging only and is never surfaced onto the scrape wire.
    /// </summary>
    public sealed class PrometheusMetricScope
    {
        private PrometheusMetricScope(bool included, IDictionary<string, object?>? userContext, string? reason)
        {
            IsIncluded = included;
            UserContext = userContext;
            Reason = reason;
        }

        /// <summary>Whether this metric is collected (true) or excluded fail-closed (false).</summary>
        public bool IsIncluded { get; }

        /// <summary>
        /// The user context the metric's aggregate runs under when <see cref="IsIncluded"/>. For a
        /// non-tenant metric this is an empty context (nothing to scope); for a tenant-scoped metric
        /// it is the fixed service identity — the scoping authority. Null when excluded.
        /// </summary>
        public IDictionary<string, object?>? UserContext { get; }

        /// <summary>The server-side-only exclusion reason when <see cref="IsIncluded"/> is false.</summary>
        public string? Reason { get; }

        public static PrometheusMetricScope Included(IDictionary<string, object?> userContext) =>
            new(true, userContext, null);

        public static PrometheusMetricScope Excluded(string reason) =>
            new(false, null, reason);
    }
}

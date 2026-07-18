using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Decides the scrape scope for each Prometheus metric per the slice-1
    /// <c>metric-security-mode</c> contract, so cross-tenant exposure is an explicit deployment
    /// decision rather than an ambient identity bypass. Given a metric's config and its table, it
    /// returns the <see cref="PrometheusMetricScope"/> the collector should run under, or an
    /// exclusion (fail-closed):
    /// <list type="bullet">
    /// <item>A NON-tenant table (no <c>tenant-filter</c>) has no tenant dimension to scope — it runs
    /// under an empty context, the same as the slice-2 collector already does.</item>
    /// <item><c>aggregate</c> mode — the aggregate runs under the fixed
    /// <see cref="PrometheusScrapeSecurityOptions.ServiceIdentity"/>, projected through
    /// <see cref="IBifrostAuthContextFactory"/>. That identity is the scoping authority: whatever it
    /// can see (its tenant, its policy grants) is what the metric exposes.</item>
    /// <item><c>per-tenant</c> mode — same fixed service identity, but the table is REJECTED unless
    /// its tenant column is a DECLARED metric label. The tenant column being a label is what makes
    /// every emitted series carry its tenant dimension (partitioned), so a scraper can never read
    /// one tenant's aggregate as an un-partitioned global total. A tenant column that is not a
    /// declared label is excluded, never silently aggregated cross-tenant.</item>
    /// </list>
    ///
    /// <para>Fail-closed in every direction: a tenant-scoped metric with no configured service
    /// identity, no declared mode, or (per-tenant) a non-partitionable table is EXCLUDED — the
    /// aggregate never runs under an empty/anonymous context that would leak global data.</para>
    /// </summary>
    public sealed class PrometheusScrapeScopeResolver
    {
        private readonly PrometheusScrapeSecurityOptions _options;
        private readonly IBifrostAuthContextFactory _authFactory;

        public PrometheusScrapeScopeResolver(
            PrometheusScrapeSecurityOptions options,
            IBifrostAuthContextFactory? authFactory = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _authFactory = authFactory ?? BifrostAuthContextFactory.Instance;
        }

        /// <summary>
        /// Resolves the scope for a single metric. Assumes the scrape credential has already been
        /// accepted by <see cref="PrometheusScrapeGate"/>; this decides ONLY how the metric's
        /// aggregate is scoped.
        /// </summary>
        public PrometheusMetricScope ResolveScope(PrometheusMetricConfig config, IDbTable table)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            if (table is null) throw new ArgumentNullException(nameof(table));

            if (!config.DeclaresMetric)
                return PrometheusMetricScope.Excluded("table declares no Prometheus metric");

            var tenantColumn = TenantColumn(table);
            if (tenantColumn is null)
                // Not tenant-scoped: no tenant dimension to scope. Run ungated (empty context),
                // exactly as the slice-2 collector does for a non-tenant table.
                return PrometheusMetricScope.Included(new Dictionary<string, object?>());

            // Tenant-scoped from here on: an explicit mode AND a fixed service identity are required.
            var mode = config.SecurityMode;
            if (string.IsNullOrEmpty(mode))
                // Slice-1 validation already rejects this at model load; keep the runtime fail-closed
                // so a tenant-scoped metric can never run without an explicit mode.
                return PrometheusMetricScope.Excluded(
                    "tenant-scoped metric declares no metric-security-mode");

            var serviceContext = ProjectServiceIdentity();
            if (serviceContext is null || serviceContext.Count == 0)
                // No fixed service identity → no scoping authority. Fail closed to NO metric rather
                // than running the aggregate under an anonymous/global context.
                return PrometheusMetricScope.Excluded(
                    "tenant-scoped metric has no configured service identity");

            if (string.Equals(mode, MetadataKeys.Metrics.SecurityModeAggregate, StringComparison.OrdinalIgnoreCase))
                return PrometheusMetricScope.Included(serviceContext);

            if (string.Equals(mode, MetadataKeys.Metrics.SecurityModePerTenant, StringComparison.OrdinalIgnoreCase))
            {
                if (!TenantColumnIsDeclaredLabel(config, table, tenantColumn))
                    return PrometheusMetricScope.Excluded(
                        "per-tenant mode requires the tenant column to be a declared metric label so " +
                        "every series is partitioned by tenant");
                return PrometheusMetricScope.Included(serviceContext);
            }

            // An unrecognized mode reads as "no explicit mode" — exclude rather than expose.
            return PrometheusMetricScope.Excluded($"unrecognized metric-security-mode '{mode}'");
        }

        /// <summary>The table's tenant-filter column (canonicalized to model casing), or null.</summary>
        private static string? TenantColumn(IDbTable table)
        {
            if (!table.Metadata.TryGetValue(MetadataKeys.Security.TenantFilter, out var raw) || raw is not string value)
                return null;
            var name = value.Trim();
            if (name.Length == 0)
                return null;
            return table.ColumnLookup.TryGetValue(name, out var column) ? column.ColumnName : name;
        }

        /// <summary>
        /// Whether the tenant column is one of the metric's declared labels. Label names are
        /// canonicalized to the column's DB casing (slice-1), so compare case-insensitively.
        /// </summary>
        private static bool TenantColumnIsDeclaredLabel(PrometheusMetricConfig config, IDbTable table, string tenantColumn) =>
            config.Labels.Any(label => string.Equals(label, tenantColumn, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Projects the configured fixed service principal through the shared auth seam. Returns
        /// null when no service identity is configured; an authenticated principal yields its full
        /// claim projection (tenant/roles/policy keys).
        /// </summary>
        private IDictionary<string, object?>? ProjectServiceIdentity()
        {
            var principal = _options.ServiceIdentity;
            if (principal?.Identity?.IsAuthenticated != true)
                return null;

            var carrier = new DefaultHttpContext { User = principal };
            return _authFactory.CreateUserContext(carrier);
        }
    }
}

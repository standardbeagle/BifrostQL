using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using BifrostQL.Core.Auth;
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
    /// <item>A table with NO identity-derived row scoping (neither <c>tenant-filter</c> nor an
    /// authorization policy) has no partition dimension to scope — it runs under an empty context,
    /// the same as the slice-2 collector already does.</item>
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
    /// <para>Fail-closed in every direction: a row-scoped metric with no configured service
    /// identity, no declared mode, or (per-tenant) a non-partitionable table is EXCLUDED — the
    /// aggregate never runs under an empty/anonymous context that would leak global data.</para>
    ///
    /// <para>"Row-scoped" means ANY identity-derived scoping mechanism, not just
    /// <c>tenant-filter</c>: a table scoped by <c>policy-row-scope</c> has the identical ambient
    /// cross-partition exposure, so it demands the identical explicit decision (invariant 11).
    /// See <see cref="HasIdentityRowScoping"/>.</para>
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
            if (tenantColumn is null && !HasIdentityRowScoping(table))
                // Not row-scoped by anything identity-derived: no partition dimension to scope.
                // Run ungated (empty context), exactly as the slice-2 collector does.
                return PrometheusMetricScope.Included(new Dictionary<string, object?>());

            // Row-scoped from here on: an explicit mode AND a fixed service identity are required.
            var mode = config.SecurityMode;
            if (string.IsNullOrEmpty(mode))
                // Slice-1 validation already rejects the tenant-filter case at model load; keep the
                // runtime fail-closed (and covering the policy case, which model-load validation
                // does not yet check) so a row-scoped metric can never run without an explicit mode.
                return PrometheusMetricScope.Excluded(
                    "row-scoped metric declares no metric-security-mode");

            var serviceContext = ProjectServiceIdentity();
            if (serviceContext is null || serviceContext.Count == 0)
                // No fixed service identity → no scoping authority. Fail closed to NO metric rather
                // than running the aggregate under an anonymous/global context.
                return PrometheusMetricScope.Excluded(
                    "row-scoped metric has no configured service identity");

            if (string.Equals(mode, MetadataKeys.Metrics.SecurityModeAggregate, StringComparison.OrdinalIgnoreCase))
                return PrometheusMetricScope.Included(serviceContext);

            if (string.Equals(mode, MetadataKeys.Metrics.SecurityModePerTenant, StringComparison.OrdinalIgnoreCase))
            {
                if (tenantColumn is null)
                    // per-tenant partitions series BY the declared tenant column. A table row-scoped
                    // only by policy has none, so there is nothing to partition by — exclude rather
                    // than emit one blended series across the policy's partitions.
                    return PrometheusMetricScope.Excluded(
                        "per-tenant mode requires the table to declare a tenant-filter column to " +
                        "partition by");
                if (!TenantColumnIsDeclaredLabel(config, table, tenantColumn))
                    return PrometheusMetricScope.Excluded(
                        "per-tenant mode requires the tenant column to be a declared metric label so " +
                        "every series is partitioned by tenant");
                return PrometheusMetricScope.Included(serviceContext);
            }

            // An unrecognized mode reads as "no explicit mode" — exclude rather than expose.
            return PrometheusMetricScope.Excluded($"unrecognized metric-security-mode '{mode}'");
        }

        /// <summary>
        /// Whether the table carries an authorization policy — the OTHER identity-derived
        /// row-scoping mechanism besides <c>tenant-filter</c>.
        ///
        /// <para>Every decision <see cref="PolicyEvaluator"/> makes is derived from the caller's
        /// identity: the table-level action grant, the column read-deny, and the row-scope
        /// expression. Under the empty/anonymous context an ungated scrape would use, a
        /// role-qualified row scope narrows NOTHING (see
        /// <c>PolicyFilterTransformer.RowScopeApplies</c> — a caller holding none of the named
        /// roles is left unscoped), so the aggregate silently spans every partition. That is the
        /// same ambient cross-partition default invariant 11 forbids for tenant-filtered tables,
        /// so the presence of a policy demands the same explicit deployment decision.</para>
        ///
        /// <para>Fail closed: a policy that cannot be parsed counts as scoping, so an
        /// unevaluable policy can never downgrade the table to "needs no decision".</para>
        ///
        /// <para>Soft-delete is deliberately NOT in this test: its filter is driven by an opt-in
        /// context flag rather than by identity, so it applies identically under any context and
        /// creates no cross-partition exposure.</para>
        /// </summary>
        private static bool HasIdentityRowScoping(IDbTable table)
        {
            try
            {
                return PolicyConfigCollector.FromTable(table).HasPolicy;
            }
            catch
            {
                return true;
            }
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

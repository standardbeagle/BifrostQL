using System.Security.Claims;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// The deployment SECURITY DECISION for the Prometheus business-metrics scrape surface.
    /// A Prometheus scrape carries no per-user identity, so exposing business metrics is made an
    /// EXPLICIT operator decision here rather than an ambient identity bypass:
    /// <list type="bullet">
    /// <item><see cref="BusinessMetricsEnabled"/> defaults to <c>false</c> — nothing is exported
    /// until the operator opts in.</item>
    /// <item><see cref="ScrapeCredential"/> is the shared secret a scraper must present; enabling
    /// the surface requires BOTH the flag AND a configured credential (<see cref="IsArmed"/>).</item>
    /// <item><see cref="ServiceIdentity"/> is the fixed service principal a tenant-scoped metric's
    /// aggregate runs under — projected through <see cref="IBifrostAuthContextFactory"/>, the same
    /// identity seam every transport gate uses, so the tenant/soft-delete/policy transformers scope
    /// the aggregate to THAT identity. It is the scoping authority; there is no anonymous default.</item>
    /// </list>
    ///
    /// <para>Per-metric scope selection (fixed service identity for <c>aggregate</c> mode vs declared
    /// tenant-label partitioning for <c>per-tenant</c> mode) is driven by the slice-1
    /// <c>metric-security-mode</c> contract and enforced by <see cref="PrometheusScrapeScopeResolver"/>.
    /// The credential gate itself is <see cref="PrometheusScrapeGate"/>. The HTTP exposition endpoint
    /// that consumes both is a later slice.</para>
    /// </summary>
    public sealed class PrometheusScrapeSecurityOptions
    {
        /// <summary>
        /// Master opt-in for the business-metrics surface. Defaults to <c>false</c>: business metrics
        /// are OFF by default and stay off until the operator both flips this AND configures a
        /// <see cref="ScrapeCredential"/>.
        /// </summary>
        public bool BusinessMetricsEnabled { get; init; }

        /// <summary>
        /// The shared secret a scraper presents (compared in constant time by
        /// <see cref="PrometheusScrapeGate"/>). Null/empty means no credential is configured, which
        /// leaves the surface disarmed regardless of <see cref="BusinessMetricsEnabled"/>.
        /// </summary>
        public string? ScrapeCredential { get; init; }

        /// <summary>
        /// The fixed service principal a tenant-scoped metric's aggregate runs under. Projected
        /// through <see cref="IBifrostAuthContextFactory"/> into the user context that scopes the
        /// aggregate. Null means no service identity is configured — a tenant-scoped metric then
        /// fails closed (excluded), never falling back to an anonymous/global aggregate.
        /// </summary>
        public ClaimsPrincipal? ServiceIdentity { get; init; }

        /// <summary>
        /// Whether the scrape surface is actually armed: enabled AND a credential is configured.
        /// A surface that is enabled but has no credential (or vice versa) is disarmed — the gate
        /// denies every scrape uniformly (fail-closed by construction).
        /// </summary>
        public bool IsArmed => BusinessMetricsEnabled && !string.IsNullOrEmpty(ScrapeCredential);
    }
}

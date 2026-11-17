namespace BifrostQL.Core.Observers;

/// <summary>
/// Unforgeable marker for the engine-metrics scrape flag
/// (<see cref="EngineMetricsQueryObserver.ScrapeInternalContextKey"/>). The value under that
/// user-context key hides a query from the engine's own metrics ONLY when it is
/// reference-equal to <see cref="Instance"/>; any bool, string or other scalar a claims
/// provider or wire payload can emit never trips the gate. Hiding a read from engine metrics
/// is an engine-only privilege: a caller holding it could make their reads, including denied
/// and error outcomes, invisible to monitoring.
///
/// Mint boundary: the BifrostQL.Core assembly plus its <c>InternalsVisibleTo</c> list
/// (BifrostQL.Core.Test, BifrostQL.Server, BifrostQL.Sqlite, BifrostQL.SqlServer,
/// BifrostQL.Benchmarks). The only production mint is the Prometheus scrape service, which
/// stamps the marker onto a copy of the scope's context for its own collection queries so a
/// scrape never measures itself. No public constructor, factory, or settable member may ever
/// be added.
/// </summary>
public sealed class EngineMetricsScrapeMarker
{
    internal static readonly EngineMetricsScrapeMarker Instance = new();

    private EngineMetricsScrapeMarker() { }

    /// <summary>True when <paramref name="userContext"/> carries the engine's own scrape marker.</summary>
    internal static bool IsPresent(IDictionary<string, object?> userContext) =>
        userContext.TryGetValue(EngineMetricsQueryObserver.ScrapeInternalContextKey, out var value)
        && ReferenceEquals(value, Instance);
}

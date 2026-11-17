using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Observers;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Auth;

/// <summary>
/// The engine-metrics scrape marker hides a query from the engine's own metrics so a scrape
/// does not measure itself. It is engine-only: a caller who could set it would make their
/// reads, including denied and error outcomes, invisible to monitoring. A provider claim or
/// wire entry under the key must never reach the user context, and any value other than the
/// engine's own marker must not hide the query.
/// </summary>
public sealed class EngineMetricsScrapeMarkerTests
{
    private static IDbModel EmptyModel() => new DbModel
    {
        Tables = [],
        Metadata = new Dictionary<string, object?>(),
    };

    private static QueryObserverContext Context(IDictionary<string, object?> userContext) => new()
    {
        Table = Substitute.For<IDbTable>(),
        Model = Substitute.For<IDbModel>(),
        UserContext = userContext,
        QueryType = QueryType.Standard,
        Path = "query.path",
        Duration = TimeSpan.FromMilliseconds(5),
    };

    [Fact]
    public void ProviderClaim_UnderScrapeKey_IsStrippedFromMappedContext()
    {
        var identity = new AppIdentity(
            id: "prober",
            provider: "oidc:test",
            claims: new Dictionary<string, object?>
            {
                [EngineMetricsQueryObserver.ScrapeInternalContextKey] = true,
            });

        var context = new IdentityContextMapper().ToUserContext(identity);

        context.Should().NotContainKey(EngineMetricsQueryObserver.ScrapeInternalContextKey);
    }

    [Fact]
    public void WireEntry_UnderScrapeKey_IsStrippedByMerger()
    {
        var userContext = new Dictionary<string, object?>();
        var wire = new Dictionary<string, object?>
        {
            [EngineMetricsQueryObserver.ScrapeInternalContextKey] = true,
            ["frontend-extra"] = 42,
        };

        WireContextMerger.Merge(userContext, wire, EmptyModel());

        userContext.Should().NotContainKey(EngineMetricsQueryObserver.ScrapeInternalContextKey);
        userContext["frontend-extra"].Should().Be(42, "the control: other wire entries still merge");
    }

    [Fact]
    public async Task BareBool_UnderScrapeKey_IsStillMeasured()
    {
        var metrics = new EngineMetrics(enabled: true);
        var observer = new EngineMetricsQueryObserver(metrics);

        await observer.OnQueryPhaseAsync(QueryPhase.AfterExecute, Context(new Dictionary<string, object?>
        {
            [EngineMetricsQueryObserver.ScrapeInternalContextKey] = true,
        }));

        metrics.Snapshot().Requests
            .Single(r => r.Operation == EngineOperation.Read && r.Outcome == EngineRequestOutcome.Success)
            .Count.Should().Be(1, "only the engine's own marker hides a query from engine metrics");
    }

    [Fact]
    public async Task EngineMarker_UnderScrapeKey_IsNotMeasured()
    {
        var metrics = new EngineMetrics(enabled: true);
        var observer = new EngineMetricsQueryObserver(metrics);

        await observer.OnQueryPhaseAsync(QueryPhase.AfterExecute, Context(new Dictionary<string, object?>
        {
            [EngineMetricsQueryObserver.ScrapeInternalContextKey] = EngineMetricsScrapeMarker.Instance,
        }));

        var snapshot = metrics.Snapshot();
        snapshot.Requests.Should().OnlyContain(r => r.Count == 0);
        snapshot.SqlDurations.Should().OnlyContain(h => h.Count == 0);
    }

    [Fact]
    public void Marker_HasNoPublicWayToMint()
    {
        var type = typeof(EngineMetricsScrapeMarker);

        type.IsSealed.Should().BeTrue();
        type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Should().BeEmpty();
        type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Should().BeEmpty("a public static member could hand the marker out");
    }
}

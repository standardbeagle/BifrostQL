using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// <see cref="RowProbeWindow"/> is the one place an adapter learns the window the SQL will
/// actually apply. Before it, adapters set <c>Limit = page + 1</c> and compared the row
/// count against their own page; <c>ResolveRowWindow</c> then clamped the LIMIT to the
/// ceiling inside SQL generation, so the count could never exceed the page and a cut
/// result read as complete (S3 listing, OData nextLink, gRPC page tokens, LDAP paging,
/// RESP SCAN, Prometheus cardinality).
/// </summary>
public sealed class RowProbeWindowTests
{
    private static IDbModel Model(int? maxQueryRows = null)
        => new DbModel
        {
            Tables = [],
            Metadata = maxQueryRows is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { [MetadataKeys.Model.MaxQueryRows] = maxQueryRows.Value.ToString() },
        };

    [Fact]
    public void PageBelowCeiling_FetchesOnePastThePage()
    {
        var window = RowProbeWindow.For(Model(maxQueryRows: 500), 100);

        window.Page.Should().Be(100);
        window.Fetch.Should().Be(101);
        window.Clamped.Should().BeFalse();
    }

    [Fact]
    public void PageAboveCeiling_NarrowsThePageSoTheSentinelStillFits()
    {
        var window = RowProbeWindow.For(Model(maxQueryRows: 500), 1000);

        window.Fetch.Should().Be(500, "the SQL clamps the LIMIT to the ceiling");
        window.Page.Should().Be(499, "the sentinel row has to fit under the ceiling too");
        window.Clamped.Should().BeTrue();
    }

    [Fact]
    public void FullFetchUnderACeiling_IsDetectedAsMore()
    {
        // The defect this type exists for: 500 rows back from a 1000-row request under a
        // 500-row ceiling. Compared against the requested page, 500 < 1000 reads as "done".
        var window = RowProbeWindow.For(Model(maxQueryRows: 500), 1000);

        window.HasMore(500).Should().BeTrue();
        window.IsCutByCeiling(500).Should().BeTrue();
        window.HasMore(499).Should().BeFalse();
    }

    [Fact]
    public void CallerLimitWithMoreRows_IsMoreButNotCutByTheCeiling()
    {
        var window = RowProbeWindow.For(Model(), 10);

        window.HasMore(11).Should().BeTrue("the sentinel came back, so a next page exists");
        window.IsCutByCeiling(11).Should().BeFalse("the caller chose this page, not the ceiling");
    }

    [Fact]
    public void Unbounded_ResolvesToTheCeiling() =>
        RowProbeWindow.For(Model(maxQueryRows: 50), -1)
            .Should().Be(RowProbeWindow.For(Model(maxQueryRows: 50), 50))
            .And.Match<RowProbeWindow>(w => w.Page == 49 && w.Fetch == 50 && w.Clamped);

    [Fact]
    public void UnboundedWithoutCeiling_UsesTheDefaultCeilingNotTheDefaultPage() =>
        RowProbeWindow.For(Model(), -1).Fetch.Should().Be(GqlObjectQuery.ResolveRowWindow(Model(), -1));

    [Fact]
    public void MaxIntRequest_DoesNotOverflow() =>
        RowProbeWindow.For(Model(maxQueryRows: 50), int.MaxValue).Fetch.Should().Be(50);

    [Fact]
    public void ZeroPage_StillProbesForExistence()
    {
        var window = RowProbeWindow.For(Model(), 0);

        window.Page.Should().Be(0);
        window.Fetch.Should().Be(1);
        window.HasMore(1).Should().BeTrue();
        window.Clamped.Should().BeFalse();
    }

    [Fact]
    public void CeilingOfOne_YieldsAnEmptyPageThatAlwaysReportsTheCut()
    {
        var window = RowProbeWindow.For(Model(maxQueryRows: 1), 10);

        window.Page.Should().Be(0);
        window.IsCutByCeiling(1).Should().BeTrue();
    }
}

using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.QueryModel;

/// <summary>
/// Pins the single AND-combinator the mutation transformer chain, the query transformer
/// service, and the query-field filter+_primaryKey merge all share (previously three
/// private copies of the same three lines).
/// </summary>
public sealed class TableFilterCombineTests
{
    private static TableFilter Leaf(string column, object value) =>
        TableFilterFactory.Equals("Orders", column, value);

    [Fact]
    public void CombineAnd_WrapsBothFiltersInAnAndNode()
    {
        var a = Leaf("tenant_id", 1);
        var b = Leaf("status", "open");

        var combined = TableFilter.CombineAnd(a, b);

        combined.FilterType.Should().Be(FilterType.And);
        combined.And.Should().HaveCount(2);
        combined.And[0].Should().BeSameAs(a, "the combinator wraps, never rewrites");
        combined.And[1].Should().BeSameAs(b);
        combined.Or.Should().BeEmpty();
    }

    [Fact]
    public void CombineAnd_NestsWhenChained()
    {
        // Folding three filters mirrors the transformer chain's sequential combining:
        // ((a AND b) AND c) — the nested shape the renderer already understands.
        var a = Leaf("a", 1);
        var b = Leaf("b", 2);
        var c = Leaf("c", 3);

        var combined = TableFilter.CombineAnd(TableFilter.CombineAnd(a, b), c);

        combined.And.Should().HaveCount(2);
        combined.And[0].FilterType.Should().Be(FilterType.And);
        combined.And[0].And.Should().Equal(a, b);
        combined.And[1].Should().BeSameAs(c);
    }
}

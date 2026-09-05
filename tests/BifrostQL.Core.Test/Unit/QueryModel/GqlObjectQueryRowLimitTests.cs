using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// The server-side row ceiling: the no-limit sentinel (-1) and any explicit limit above the
/// ceiling clamp to the model's max-query-rows metadata (default
/// <see cref="GqlObjectQuery.DefaultMaxQueryRows"/>), so no client can materialize an
/// unbounded table read. An explicit limit: 0 is preserved — only unbounded or
/// over-ceiling requests are clamped. A null limit resolves to
/// <see cref="GqlObjectQuery.DefaultRowWindow"/> INSIDE
/// <see cref="GqlObjectQuery.ResolveRowWindow"/> (see ResolveRowWindowTests), so the
/// ceiling binds an unspecified limit too.
/// </summary>
public sealed class GqlObjectQueryRowLimitTests
{
    private static IDbModel Model(int? maxQueryRows = null)
        => new DbModel
        {
            Tables = [],
            Metadata = maxQueryRows is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { [MetadataKeys.Model.MaxQueryRows] = maxQueryRows.Value.ToString() },
        };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9_999, 9_999)]
    public void BoundedLimits_PassThroughUnchanged(int? limit, int expected) =>
        GqlObjectQuery.ResolveRowWindow(Model(), limit).Should().Be(expected);

    [Fact]
    public void NoLimitSentinel_ClampsToDefaultCeiling() =>
        GqlObjectQuery.ResolveRowWindow(Model(), -1).Should().Be(GqlObjectQuery.DefaultMaxQueryRows,
            "limit: -1 must not mean 'read the entire table'");

    [Fact]
    public void ExplicitLimitAboveCeiling_ClampsToCeiling() =>
        GqlObjectQuery.ResolveRowWindow(Model(), 1_000_000).Should().Be(GqlObjectQuery.DefaultMaxQueryRows);

    [Fact]
    public void NoLimitSentinel_ClampsToConfiguredCeiling()
    {
        var ceiling = GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 50), -1);
        ceiling.Should().Be(50);
        GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 50), 100).Should().Be(50);
    }

    [Fact]
    public void InvalidCeilingMetadata_ThrowsInsteadOfSilentlyDefaulting() =>
        FluentActions.Invoking(() => GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 0), -1))
            .Should().Throw<InvalidOperationException>(
                "a non-positive max-query-rows is an operator typo — fail fast, never silently disable the ceiling");
}

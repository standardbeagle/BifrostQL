using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Refactor 01M1QX52DNJTN6CBA9P6ZMBECM: the row-window default is folded into the
/// resolver. <c>ClampRowLimit(IDbModel, int?)</c> returned a null limit UNCHANGED, so
/// a call site forwarding a bare null fell through to the dialect's null → 100 default
/// BELOW the ceiling — under <c>max-query-rows: 5</c> the read still returned 100 rows
/// (shipped four times: H7 grouped aggregate, M9 root SELECT + restricted join-id
/// sub-query, fe6f9129 per-parent paged collection). <c>ResolveRowWindow</c> returns a
/// NON-nullable window — <c>requested ?? DefaultRowWindow</c>, clamped to the ceiling —
/// so the bypass is unreachable by construction.
/// </summary>
public sealed class ResolveRowWindowTests
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
    public void NoLimit_UnderCeilingOfFive_ResolvesToTheCeiling() =>
        GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 5), null).Should().Be(5,
            "a call site passing NO limit must get the ceiling, not the dialect's null → 100 default");

    [Fact]
    public void NoLimit_WithoutCeiling_ResolvesToTheDefaultWindow() =>
        GqlObjectQuery.ResolveRowWindow(Model(), null).Should().Be(GqlObjectQuery.DefaultRowWindow);

    [Fact]
    public void NoLimitSentinel_ResolvesToTheCeiling() =>
        GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 50), -1).Should().Be(50,
            "limit: -1 is the explicit unbounded sentinel — it clamps to the ceiling, never to an unbounded read");

    [Fact]
    public void ZeroLimit_StaysEmptyBounded() =>
        GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 5), 0).Should().Be(0,
            "limit: 0 is an empty page, not the default window");

    [Fact]
    public void ExplicitLimitBelowCeiling_Narrows() =>
        GqlObjectQuery.ResolveRowWindow(Model(maxQueryRows: 50), 2).Should().Be(2);

    /// <summary>
    /// The nullable-passthrough shape is REMOVED, not kept as a fallback: no source
    /// file may still name <c>ClampRowLimit</c>, and the single new entry point must
    /// exist. A pure negative scan is vacuous, so the positive hit is asserted too.
    /// </summary>
    [Fact]
    public void ClampRowLimit_IsRemovedFromSource()
    {
        var srcDir = RepoDir("src");
        var sources = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToList();

        sources.Should().Contain(s => s.Contains("ResolveRowWindow"),
            "the scan must be non-vacuous — the new entry point exists");
        sources.Should().NotContain(s => s.Contains("ClampRowLimit"),
            "the null-passthrough overload is removed, not kept as a fallback");
    }

    private static string RepoDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, name, "BifrostQL.Core")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from within the repository checkout");
        return Path.Combine(dir!.FullName, name);
    }
}

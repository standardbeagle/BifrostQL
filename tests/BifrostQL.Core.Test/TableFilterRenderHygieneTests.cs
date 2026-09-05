// Hygiene guard: TableFilter's single-fragment render (which concatenated
// "{joins} WHERE {where}" into one ParameterizedSql) was deleted — splicing that
// fragment after a hard WHERE produced "WHERE INNER JOIN ..." (finding 01M1KNYPDYXR158JAQNDQNFMFG
// item 2, fixed in 5cb28e2d). RenderParts is the only render entry point now, so a
// filter's joins and predicates can never be re-spliced into the wrong clause. This
// scan fails if the identifier returns to TableFilter.cs or any filter-typed call
// site, and proves it is not vacuous by requiring positive RenderParts hits in the
// test tree. The same-named GqlAggregateColumn / GroupedAggregateQuery overloads are
// different types and out of scope.
using System.Text.RegularExpressions;
using Xunit;

namespace BifrostQL.Core.Test;

public class TableFilterRenderHygieneTests
{
    private static readonly Regex FilterCallSite = new(
        @"[Ff]ilter!?\s*\.ToSqlParameterized\s*\(", RegexOptions.Compiled);

    [Fact]
    public void TableFilter_HasNoToSqlParameterizedRender()
    {
        var repoRoot = FindRepoRoot();
        var tableFilter = Path.Combine(repoRoot, "src", "BifrostQL.Core", "QueryModel", "TableFilter.cs");
        Assert.True(File.Exists(tableFilter), $"TableFilter.cs not found: {tableFilter}");

        var source = File.ReadAllText(tableFilter);
        Assert.DoesNotContain("ToSqlParameterized", source);
    }

    [Fact]
    public void NoFilterTypedToSqlParameterizedCallSites_AndRenderPartsIsUsed()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();
        var renderPartsHits = 0;
        foreach (var root in new[]
        {
            Path.Combine(repoRoot, "src"),
            Path.Combine(repoRoot, "tests"),
        })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///"))
                        continue;
                    if (FilterCallSite.IsMatch(lines[i]))
                        offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                    if (lines[i].Contains(".RenderParts("))
                        renderPartsHits++;
                }
            }
        }

        // The scan must SEE the replacement entry point: zero hits everywhere would
        // also be "zero offenders", so a pattern that stopped matching real code
        // would stay vacuously green.
        Assert.True(renderPartsHits > 0,
            "Expected positive .RenderParts( hits in src/tests; the scan no longer matches real code.");
        Assert.True(offenders.Count == 0,
            "The single-fragment TableFilter render was deleted; render via RenderParts and assemble Joins/Where into their own clauses:\n"
            + string.Join("\n", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "Could not locate repo root (BifrostQL.sln) above the test assembly.");
        return dir!.FullName;
    }
}

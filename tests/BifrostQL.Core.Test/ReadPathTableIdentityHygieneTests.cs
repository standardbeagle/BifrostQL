// Hygiene guard for finding M11: the read path used to resolve tables by a BARE
// DbName (`dbModel.GetTableFromDbName(TableName)` in GqlObjectQuery and TableFilter).
// DbModel rejects an ambiguous bare name, so a model carrying `sales.orders` AND
// `archive.orders` made every query, join, filter, aggregate and pivot on EITHER
// table throw. Read-path table identity is now carried as `IDbTable` (TableFilter.Table,
// GqlObjectQuery.DbTable, TableJoin.ConnectedTable.DbTable), so no schema is ever
// re-derived from a name.
//
// This scan fails if a bare-name lookup returns to the read path, and proves it is not
// vacuous by requiring positive hits on the schema-qualified overload — zero hits
// everywhere would also read as "zero offenders" if the pattern stopped matching real
// code. It is anchored on the API name itself, which a copier cannot rename.
using Xunit;

namespace BifrostQL.Core.Test;

public class ReadPathTableIdentityHygieneTests
{
    // Write/file entry points resolve a client-supplied table name ONCE at the wire
    // boundary and thread the resolved IDbTable onward; they are the mutation path,
    // not the read path M11 covers. Listed explicitly so re-adding a bare lookup to a
    // READ file cannot hide behind a directory-wide exemption.
    private static readonly string[] Allowlist =
    [
        "src/BifrostQL.Core/Resolvers/MutationIntentExecutor.cs",
        "src/BifrostQL.Core/Resolvers/FileUploadResolver.cs",
        "src/BifrostQL.Core/Resolvers/FileDownloadResolver.cs",
        "src/BifrostQL.Core/Resolvers/FileDeleteResolver.cs",
    ];

    [Fact]
    public void ReadPath_ResolvesNoTableByBareName_AndUsesTheSchemaQualifiedOverload()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();
        var qualifiedHits = 0;

        foreach (var dir in new[] { "QueryModel", "Resolvers" })
        {
            var root = Path.Combine(repoRoot, "src", "BifrostQL.Core", dir);
            Assert.True(Directory.Exists(root), $"Read-path directory not found: {root}");

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                    continue;

                // Scan the WHOLE file, not line by line: a call whose argument list wraps
                // onto the next line (`GetTableFromDbName(\n    name)`) is still a bare
                // lookup, and a per-line scan cannot see its arity. Comment lines are
                // blanked (not removed) so reported line numbers stay real.
                var lines = File.ReadAllLines(file);
                var code = string.Join("\n", lines.Select(l =>
                {
                    var trimmed = l.TrimStart();
                    return trimmed.StartsWith("//") || trimmed.StartsWith("*") ? "" : l;
                }));

                foreach (var (argCount, at) in LookupArity(code))
                {
                    if (argCount >= 2)
                    {
                        qualifiedHits++;
                        continue;
                    }
                    if (Array.Exists(Allowlist, a => a == relative))
                        continue;
                    var lineNo = code.AsSpan(0, at).Count('\n') + 1;
                    offenders.Add($"{relative}:{lineNo}: {lines[lineNo - 1].Trim()}");
                }
            }
        }

        Assert.True(qualifiedHits > 0,
            "Expected positive schema-qualified GetTableFromDbName(schema, name) hits on the read path; "
            + "the scan no longer matches real code and would pass vacuously.");
        Assert.True(offenders.Count == 0,
            "Read-path table identity must be carried as IDbTable, never re-resolved from a bare DbName "
            + "(ambiguous across schemas — finding M11). Thread the IDbTable, or use the schema-qualified "
            + "GetTableFromDbName(schema, name):\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Yields, for every <c>GetTableFromDbName</c> / <c>TryGetTableFromDbName</c> call in
    /// the text, the count of its INPUT arguments and the offset of the call — commas are
    /// split at paren/bracket depth one so a nested call is not mistaken for a second
    /// argument, an <c>out</c> argument is not counted (the Try* overloads carry one, so
    /// counting it would make a bare-name `Try(name, out t)` look schema-qualified), and
    /// the argument list may span lines. An argument list the file never closes is
    /// reported as a bare call rather than silently trusted.
    /// </summary>
    private static IEnumerable<(int Inputs, int At)> LookupArity(string code)
    {
        const string name = "GetTableFromDbName";
        var from = 0;
        while (true)
        {
            var at = code.IndexOf(name, from, StringComparison.Ordinal);
            if (at < 0) yield break;
            from = at + name.Length;

            var open = from;
            while (open < code.Length && char.IsWhiteSpace(code[open])) open++;
            if (open >= code.Length || code[open] != '(') continue;

            var depth = 0;
            var closed = false;
            var args = new List<string>();
            var start = open + 1;
            var end = code.Length;
            for (var i = open; i < code.Length; i++)
            {
                var c = code[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']')
                {
                    depth--;
                    if (depth == 0) { end = i; closed = true; break; }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(code[start..i]);
                    start = i + 1;
                }
            }
            args.Add(code[start..end]);

            // A declaration/reference with an empty parameter list is not a call site.
            if (args.Count == 1 && string.IsNullOrWhiteSpace(args[0])) continue;
            if (!closed) { yield return (0, at); continue; }

            var inputs = args.Count(a => !a.TrimStart().StartsWith("out ", StringComparison.Ordinal));
            yield return (inputs, at);
        }
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

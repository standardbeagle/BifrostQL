// Hygiene guard: ToolJson.ParseKeyValues is the ONLY home of the arity-aware
// '|' key-splitting rule in the MCP assembly. A consolidation claim proven only
// by a diff is a claim about files the diff does not contain — the second copy
// in DeclarativeQueryToolCompiler (fixed in 1404a6c7) was found by a reviewer
// reading sibling files. This walks src/BifrostQL.Mcp/**/*.cs and fails on any
// '|' key-split shape outside ToolJson.cs. The allowlist is the single file
// NAME, not a growable list of exemptions.
using System.Text.RegularExpressions;
using Xunit;

namespace BifrostQL.Mcp.Test;

public class KeyParserHygieneTests
{
    private const string AllowedFileName = "ToolJson.cs";

    private static readonly (string Id, Regex Pattern)[] Shapes =
    {
        ("Split-char", new Regex(@"\.Split\(\s*'\|'", RegexOptions.Compiled)),
        ("Split-string", new Regex(@"\.Split\(\s*""\|""", RegexOptions.Compiled)),
        ("Split-array", new Regex(@"\.Split\(\s*new(\[\])?\s*(char)?\s*\[\]\s*\{\s*'\|'", RegexOptions.Compiled)),
        ("Split-collection", new Regex(@"\.Split\(\s*\[\s*'\|'", RegexOptions.Compiled)),
        ("IndexOf-pipe", new Regex(@"\.IndexOf\(\s*'\|'", RegexOptions.Compiled)),
    };

    [Fact]
    public void OnlyToolJsonSplitsKeyValuesOnPipe()
    {
        var repoRoot = FindRepoRoot();
        var mcpSrc = Path.Combine(repoRoot, "src", "BifrostQL.Mcp");
        Assert.True(Directory.Exists(mcpSrc), $"MCP source directory not found: {mcpSrc}");

        var offenders = new List<string>();
        var allowedHits = 0;
        foreach (var file in Directory.EnumerateFiles(mcpSrc, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                continue;
            var allowed = Path.GetFileName(file) == AllowedFileName;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///"))
                    continue;
                foreach (var (id, pattern) in Shapes)
                {
                    if (!pattern.IsMatch(lines[i]))
                        continue;
                    if (allowed)
                        allowedHits++;
                    else
                        offenders.Add($"{relative}:{i + 1} ({id}): {lines[i].Trim()}");
                }
            }
        }

        // The scan must SEE the one legitimate split: zero hits everywhere would also be
        // "zero offenders", so a pattern set that stopped matching real code stays vacuously green.
        Assert.True(allowedHits > 0,
            $"Expected {AllowedFileName} to contain the '|' key split (ToolJson.ParseKeyValues); the scan patterns no longer match real code.");
        Assert.True(offenders.Count == 0,
            "'|' key splitting outside ToolJson.cs. Use ToolJson.ParseKeyValues — the single home of the arity-aware key-split rule:\n"
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

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Transformer-chain callers must not grow their own keyed SQL or key split.
/// FilteredUpdatePipeline is intentionally in the population but is not a keyed
/// seam: its TableFilter-composed predicate has no primary-key contract. The
/// staged and dialect bulk executors are outside the population because they
/// consume the plan produced by MutationArgumentBinder and never call the chain.
/// </summary>
public sealed class KeyedWriteSourceScanTests
{
    private static readonly Regex StatementText = new(
        @"(?<![A-Za-z])(?:UPDATE\s|DELETE\s+FROM|WHERE\s|SELECT\s+1\s+FROM|SELECT\s+COUNT\()",
        RegexOptions.Compiled);

    private static readonly Regex KeySplit = new(
        @"\.Where\s*\(\s*(?:\([^)]*\)\s*=>|[A-Za-z_]\w*\s*=>)\s*!?\s*(?:DbParameterBinder\.)?IsPrimaryKeyColumn\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void TransformerChainCallers_DoNotBuildKeyedSqlOrSplitKeys()
    {
        var sourceRoot = LocateBifrostCoreSourceRoot();
        sourceRoot.Should().NotBeNull();

        var anchored = new List<(string RelativePath, string Text)>();
        foreach (var directory in new[] { "Resolvers", "Modules", "Storage" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(sourceRoot!, directory), "*.cs", SearchOption.AllDirectories))
            {
                var text = StripComments(File.ReadAllText(path));
                if (text.Contains(".TransformAsync(", StringComparison.Ordinal))
                    anchored.Add((Path.GetRelativePath(sourceRoot!, path).Replace(Path.DirectorySeparatorChar, '/'), text));
            }
        }

        (anchored.Count >= 6).Should().BeTrue(
            "the anchor population must remain large enough to catch a drifted anchor pattern");

        var positive = anchored.SelectMany(file => Hits(file.RelativePath, file.Text)).ToList();
        positive.Should().NotBeEmpty("a scan that matches nothing is vacuous");

        var executor = positive.Where(hit => hit.StartsWith("Resolvers/MutationCommandExecutor.cs:", StringComparison.Ordinal)).ToList();
        executor.Should().Contain(hit => hit.Contains("UPDATE", StringComparison.Ordinal));
        executor.Should().Contain(hit => hit.Contains("DELETE FROM", StringComparison.Ordinal));
        executor.Should().Contain(hit => hit.Contains("SELECT 1 FROM", StringComparison.Ordinal)
                                      || hit.Contains("SELECT COUNT(", StringComparison.Ordinal));

        var binderPath = Path.Combine(sourceRoot!, "Resolvers", "MutationArgumentBinder.cs");
        var binderText = StripComments(File.ReadAllText(binderPath));
        KeySplit.IsMatch(binderText)
            .Should().BeTrue("MutationArgumentBinder must prove the shared key split exists");

        positive.Where(hit => !hit.StartsWith("Resolvers/MutationCommandExecutor.cs:", StringComparison.Ordinal))
            .Should().BeEmpty("every other transformer-chain caller must contain no hand-built keyed SQL or key split");
    }

    private static IEnumerable<string> Hits(string relativePath, string text)
    {
        foreach (Match match in StatementText.Matches(text))
            yield return $"{relativePath}:{Line(text, match.Index)}: statement text ({match.Value.Trim()})";

        foreach (Match match in KeySplit.Matches(text))
            yield return $"{relativePath}:{Line(text, match.Index)}: key split";
    }

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                output.Append('\n');
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') output.Append('\n');
                    else output.Append(' ');
                    i++;
                }
                i++;
                output.Append(' ');
            }
            else if (c == '"' || c == '\'')
            {
                var quote = c;
                output.Append(c);
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    output.Append(source[i]);
                    if (source[i] == '\\' && i + 1 < source.Length) output.Append(source[++i]);
                    i++;
                }
                if (i < source.Length) output.Append(source[i]);
            }
            else output.Append(c);
        }
        return output.ToString();
    }

    private static string? LocateBifrostCoreSourceRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(callerFilePath)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BifrostQL.Core");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}

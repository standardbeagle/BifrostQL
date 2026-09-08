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
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StringLiteral = new(
        "(?:@\\\"(?:\\\"\\\"|[^\\\"])*\\\"|\\\"(?:\\\\.|[^\\\"\\\\])*\\\")",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
        (executor.Count >= 3).Should().BeTrue(
            "MutationCommandExecutor must prove update, delete, and exists/count statement-text hits");

        var binderPath = Path.Combine(sourceRoot!, "Resolvers", "MutationArgumentBinder.cs");
        var binderText = StripComments(File.ReadAllText(binderPath));
        KeySplit.IsMatch(binderText)
            .Should().BeTrue("MutationArgumentBinder must prove the shared key split exists");

        positive.Where(hit => !hit.StartsWith("Resolvers/MutationCommandExecutor.cs:", StringComparison.Ordinal))
            .Where(hit => !hit.StartsWith("Resolvers/FilteredUpdatePipeline.cs:", StringComparison.Ordinal))
            .Should().BeEmpty("every other transformer-chain caller must contain no hand-built keyed SQL or key split");
    }

    private static IEnumerable<string> Hits(string relativePath, string text)
    {
        foreach (Match literal in StringLiteral.Matches(text))
        {
            foreach (Match match in StatementText.Matches(literal.Value))
                yield return $"{relativePath}:{Line(text, literal.Index + match.Index)}: statement text ({match.Value.Trim()})";
        }

        foreach (Match match in KeySplit.Matches(text))
            yield return $"{relativePath}:{Line(text, match.Index)}: key split";
    }

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", match => new string('\n', match.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", string.Empty);
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

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// The file mutation resolvers must reach the database ONLY through the shared
/// seams — the read-intent executor for the pointer read, and
/// <c>TableMutationPipeline</c> for the pointer write. Hand-rolled SQL in these
/// resolvers is what let them skip the history-target guard, before-commit hooks
/// (approval), in-transaction hooks (history, CDC outbox), the transaction and the
/// cancellation token, and what let their <c>@{k}</c> placeholders bypass
/// <c>SqlParameterNames.Sanitize</c> (finding H2).
///
/// A source scan is the only durable guard here: a behavioural test can only prove
/// that today's paths run the pipeline, not that tomorrow's added helper does not
/// open a second, ungated path to SQL.
/// </summary>
public class FileResolverNoSqlTextTests
{
    // SQL statement text as it appears in these resolvers' interpolated command
    // strings. Deliberately anchored on statement keywords rather than the word
    // "sql", so a comment mentioning SQL does not trip the scan.
    private static readonly Regex SqlText = new(
        @"\b(SELECT\s|UPDATE\s|INSERT\s+INTO|DELETE\s+FROM|WHERE\s|CommandText)\b",
        RegexOptions.Compiled);

    private static readonly string[] FileMutationResolvers =
    {
        "Resolvers/FileUploadResolver.cs",
        "Resolvers/FileDeleteResolver.cs",
    };

    [Fact]
    public void FileMutationResolvers_BuildNoSqlText()
    {
        var sourceRoot = LocateBifrostCoreSourceRoot();
        sourceRoot.Should().NotBeNull(
            "the BifrostQL.Core source directory must be locatable from the test assembly");

        var violations = new List<string>();
        foreach (var relative in FileMutationResolvers)
        {
            var path = Path.Combine(sourceRoot!, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"expected resolver source file '{relative}' to exist");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                if (SqlText.IsMatch(code))
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        violations.Should().BeEmpty(
            "the file mutation resolvers must route reads through the query-intent seam and writes through "
            + "TableMutationPipeline, never build SQL themselves:\n" + string.Join("\n", violations));
    }

    // Doc comments and line comments legitimately name SQL statements when they
    // explain which seam owns them; only executable text is scanned.
    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static string? LocateBifrostCoreSourceRoot([CallerFilePath] string callerFilePath = "")
    {
        if (string.IsNullOrEmpty(callerFilePath))
            return null;

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

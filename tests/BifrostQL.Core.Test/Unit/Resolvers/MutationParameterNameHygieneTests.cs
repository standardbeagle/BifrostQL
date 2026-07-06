using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Guards the mutation WHERE/SET parameter-name contract. Every write path must
/// render its <c>@parameter</c> placeholder through
/// <c>SqlParameterNames.Sanitize</c>, because <c>DbParameterBinder.AddParameters</c>
/// binds the parameter under the sanitized name. A raw <c>@{kv.Key}</c> (or any
/// direct column-name interpolation) desynchronizes the two: a table with a key
/// or column whose name is not a valid ADO identifier (e.g. "Order Date") then
/// fails every UPDATE/DELETE at runtime. This source scan fails loudly if the
/// raw pattern reappears — the resolvers build these clauses as inline
/// interpolated strings, so there is no single function to unit-test instead.
/// </summary>
public class MutationParameterNameHygieneTests
{
    // Matches an interpolated parameter placeholder whose name comes straight
    // from a column key (…@{kv.Key}, @{d.Key}, @{k}) with no Sanitize wrapper.
    private static readonly Regex RawKeyPlaceholder = new(
        @"@\{[A-Za-z_][A-Za-z0-9_]*(\.Key)?\}",
        RegexOptions.Compiled);

    // Write-path files that build mutation SQL by interpolation.
    private static readonly string[] WritePathFiles =
    {
        "Resolvers/DbTableMutateResolver.cs",
        "Resolvers/DbTableBatchResolver.cs",
        "Resolvers/MutationCommandExecutor.cs",
        "Resolvers/ResolverBase.cs",
        "Modules/TreeSyncExecutor.cs",
    };

    [Fact]
    public void WritePaths_NeverInterpolateRawColumnNameAsParameter()
    {
        var sourceRoot = LocateBifrostCoreSourceRoot();
        sourceRoot.Should().NotBeNull(
            "the BifrostQL.Core source directory must be locatable from the test assembly");

        var violations = new List<string>();
        foreach (var relative in WritePathFiles)
        {
            var path = Path.Combine(sourceRoot!, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"expected write-path source file '{relative}' to exist");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Only care about parameter placeholders (preceded by '='/'(' and '@').
                foreach (Match m in RawKeyPlaceholder.Matches(line))
                {
                    // A placeholder is fine when it is the argument to Sanitize(...).
                    var idx = m.Index;
                    var before = line[..idx];
                    if (before.TrimEnd().EndsWith("Sanitize(", StringComparison.Ordinal))
                        continue;
                    violations.Add($"{relative}:{i + 1}: {line.Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "mutation parameter placeholders must go through SqlParameterNames.Sanitize so they match the bound parameter names:\n"
            + string.Join("\n", violations));
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

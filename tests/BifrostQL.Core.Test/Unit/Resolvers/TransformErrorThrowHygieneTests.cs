using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Guards the mutation-denial condition-tagging contract. Every mutation execution
/// path must abort a transformer-denied write through
/// <c>MutationTransformResult.ThrowIfDenied()</c>, which carries the result's
/// <c>ErrorCode</c> onto the thrown <c>BifrostExecutionError</c>. A hand-rolled
/// <c>throw new BifrostExecutionError(string.Join("; ", ...Errors))</c> that forgets
/// <c>{ ErrorCode = ... }</c> silently downgrades a policy/tenant denial to a generic
/// INTERNAL on one op class — the cross-op-class divergence
/// .claude/rules/protocol-adapter-security.md rule 10 exists to prevent, and a bug
/// that recurred independently across the batch pipeline and both file resolvers.
///
/// This source scan fails loudly if the raw pattern reappears anywhere in
/// BifrostQL.Core, so the next author is pushed to the shared helper instead of
/// re-deriving (and re-breaking) the throw. Mirrors
/// <see cref="MutationParameterNameHygieneTests"/>'s source-scan approach.
/// </summary>
public class TransformErrorThrowHygieneTests
{
    // Matches a throw that joins a transformer result's Errors into a
    // BifrostExecutionError message — the hand-rolled shape ThrowIfDenied replaces.
    private static readonly Regex HandRolledThrow = new(
        @"new BifrostExecutionError\(\s*string\.Join\(""; "",\s*[A-Za-z_][A-Za-z0-9_]*\.Errors\)",
        RegexOptions.Compiled);

    [Fact]
    public void CoreSources_NeverHandRollAthrowOverTransformErrors()
    {
        var sourceRoot = LocateBifrostCoreSourceRoot();
        sourceRoot.Should().NotBeNull(
            "the BifrostQL.Core source directory must be locatable from the test assembly");

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(sourceRoot!, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build artifacts.
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (HandRolledThrow.IsMatch(lines[i]))
                {
                    var relative = Path.GetRelativePath(sourceRoot!, path);
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "a transformer-denied mutation must abort through MutationTransformResult.ThrowIfDenied() "
            + "so the denial keeps its ErrorCode on the wire — never a hand-rolled "
            + "throw new BifrostExecutionError(string.Join(\"; \", result.Errors)):\n"
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

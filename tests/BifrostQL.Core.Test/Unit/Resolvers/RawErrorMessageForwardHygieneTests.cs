using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Guards against forwarding a raw caught-exception message onto a client-facing
/// error. A <c>new BifrostExecutionError($"...{ex.Message}...", ex)</c> built from a
/// database/driver/storage exception leaks infrastructure detail (schema names,
/// identifiers, server paths, login/permission text) to the client — the exact leak
/// .claude/rules/protocol-adapter-security.md rule 3 flags. Database exceptions must
/// go through <c>BifrostExecutionError.FromDatabaseException(ex)</c>, which redacts
/// the raw text (unless BIFROST_EXPOSE_DB_ERRORS is set), classifies a
/// unique/duplicate-key violation as a stable CONFLICT, and keeps the original as
/// InnerException for server-side logging.
///
/// This source scan fails loudly if the raw pattern reappears anywhere in
/// BifrostQL.Core, so the next author is pushed to the redaction seam. It recurred
/// across seven resolver sites (_rawQuery, the generic-table query, and both file
/// resolvers' read/write paths). Mirrors
/// <see cref="MutationParameterNameHygieneTests"/>'s source-scan approach.
/// </summary>
public class RawErrorMessageForwardHygieneTests
{
    // Matches a BifrostExecutionError whose interpolated message embeds some
    // exception variable's .Message — the leak shape FromDatabaseException replaces.
    private static readonly Regex ForwardsRawMessage = new(
        @"new BifrostExecutionError\(\$""[^""]*\{[A-Za-z_][A-Za-z0-9_]*\.Message\}",
        RegexOptions.Compiled);

    [Fact]
    public void CoreSources_NeverForwardARawExceptionMessageToAClientError()
    {
        var sourceRoot = LocateBifrostCoreSourceRoot();
        sourceRoot.Should().NotBeNull(
            "the BifrostQL.Core source directory must be locatable from the test assembly");

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(sourceRoot!, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (ForwardsRawMessage.IsMatch(lines[i]))
                {
                    var relative = Path.GetRelativePath(sourceRoot!, path);
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "a caught database/driver/storage exception must be surfaced through "
            + "BifrostExecutionError.FromDatabaseException(ex) so its raw text is redacted — "
            + "never a hand-built new BifrostExecutionError($\"...{ex.Message}...\"):\n"
            + string.Join("\n", violations));
    }

    // Matches forwarding a caught exception's .Message into a GraphQL ExecutionError:
    // `new ExecutionError(ex.Message` (with or without the inner-exception argument).
    // Deliberately does NOT match `new BifrostExecutionError(` — that shape is covered
    // by the interpolation scan above and by provenance review at its throw sites.
    private static readonly Regex ForwardsMessageToExecutionError = new(
        @"new ExecutionError\(\s*[A-Za-z_][A-Za-z0-9_]*\.Message",
        RegexOptions.Compiled);

    private static readonly Regex CatchClause = new(@"catch\s*\(\s*([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    /// <summary>
    /// `new ExecutionError(ex.Message, ...)` is the designed GraphQL error channel for
    /// BifrostExecutionError ONLY — that type's text is curated at its throw sites
    /// (FromDatabaseException redacts driver text; transformer denials are authored).
    /// The same forward from any OTHER catch (ArgumentException, InvalidOperationException,
    /// DbException, ...) puts ambient driver/BCL fault text on the wire — the
    /// PivotTableResolver leak shape, where a `catch (ArgumentException)` meant to cover
    /// config shaping also spanned the executor call. This scan pins that every such
    /// forward sits inside a `catch (BifrostExecutionError ...)`.
    /// </summary>
    [Fact]
    public void CoreSources_ForwardExecutionErrorMessagesOnlyFromBifrostExecutionErrorCatches()
    {
        var sourceRoot = LocateBifrostCoreSourceRoot();
        sourceRoot.Should().NotBeNull();

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(sourceRoot!, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!ForwardsMessageToExecutionError.IsMatch(lines[i]))
                    continue;

                // Walk back to the nearest enclosing catch clause; the forward is legal
                // only when that clause catches BifrostExecutionError.
                var caughtType = (string?)null;
                for (var j = i; j >= 0 && j >= i - 20; j--)
                {
                    var m = CatchClause.Match(lines[j]);
                    if (m.Success)
                    {
                        caughtType = m.Groups[1].Value;
                        break;
                    }
                }

                if (caughtType != "BifrostExecutionError")
                {
                    var relative = Path.GetRelativePath(sourceRoot!, path);
                    violations.Add($"{relative}:{i + 1} (catch: {caughtType ?? "none found"}): {lines[i].Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "only a caught BifrostExecutionError's message is curated for the wire; forwarding "
            + "any other exception's .Message into an ExecutionError leaks ambient driver/BCL text:\n"
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

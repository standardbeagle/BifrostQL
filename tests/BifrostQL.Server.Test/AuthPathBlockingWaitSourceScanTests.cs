using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The identity projection (grant resolver) and the policy gate (model load) are asynchronous
/// end to end; no file on the auth path may block a thread on a task. The scanned set is every
/// BifrostQL.Server / BifrostQL.Mcp source file whose CODE touches the auth seam types, so a new
/// gate joins the scan by using the seam, not by being listed here. Comments are stripped and
/// string/char literal contents blanked by walking the literals, and the whole file is scanned
/// as one string so a call wrapped across lines is still seen.
/// </summary>
public sealed class AuthPathBlockingWaitSourceScanTests
{
    /// <summary>The auth seam: a file whose code names one of these is on the auth path.</summary>
    private static readonly Regex AuthSeam = new(
        @"\b(?:IGrantResolver|IBifrostAuthContextFactory|BifrostAuthContextFactory|BifrostIdentityGate|"
        + @"AdapterIdentityProjection|GrpcIdentityGate|IPolicyGateAccessor|PolicyGate)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Thread-blocking waits on a task or awaiter: <c>.GetResult()</c> on an awaiter,
    /// <c>.Wait(…)</c>, and <c>.Result</c> read off a call or a task-named value. A bare
    /// <c>.Result</c> member (an enum value such as <c>BifrostMessageType.Result</c>) is not a wait.
    /// </summary>
    private static readonly Regex BlockingWait = new(
        @"\.\s*GetResult\s*\(|\.\s*Wait\s*\(|(?:\)|\b\w*[Tt]ask)\s*\.\s*Result\b",
        RegexOptions.Compiled);

    [Fact]
    public void AuthPathSources_NeverBlockOnATask()
    {
        var repoRoot = LocateRepoRoot();
        var files = new[] { "BifrostQL.Server", "BifrostQL.Mcp" }
            .SelectMany(project => Directory.GetFiles(Path.Combine(repoRoot, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => f, f => CodeOnly(File.ReadAllText(f)));

        var authPath = files.Where(f => AuthSeam.IsMatch(f.Value)).ToDictionary(f => Path.GetFileName(f.Key), f => f.Value);

        // Positive anchors: the two seams this scan exists for, and the funnels around them, are
        // in the set. A drifted AuthSeam pattern empties the set and fails here, not GREEN.
        authPath.Keys.Should().Contain(new[]
        {
            "BifrostAuthContextFactory.cs", "BifrostIdentityGate.cs", "PolicyGateAccessor.cs",
            "BifrostServiceRegistrar.cs", "ProtocolAdapter.cs", "GrpcIdentityGate.cs", "BifrostMcpAdapter.cs",
        });
        authPath.Count.Should().BeGreaterThan(20, "every transport gate projects through the seam");

        var offenders = authPath
            .Where(f => BlockingWait.IsMatch(f.Value))
            .Select(f => $"{f.Key}: {BlockingWait.Match(f.Value).Value}")
            .ToList();
        offenders.Should().BeEmpty(
            "the grant resolver and the policy-gate model load are awaited, never blocked on "
            + "(a blocked request thread starves the pool). Offenders: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("var g = resolver.ResolveAsync(i, ct).AsTask().GetAwaiter().GetResult();")]
    [InlineData("var g = resolver.ResolveAsync(i, ct).AsTask()\n    .GetAwaiter()\n    .GetResult();")]
    [InlineData("var s = \"//\"; load.Wait();")]
    [InlineData("var c = '\"'; var m = cache.GetValueAsync(p).Result;")]
    [InlineData("var m = modelTask.Result;")]
    public void BlockingWait_MatchesWaitsInCode(string code)
        => BlockingWait.IsMatch(CodeOnly(code)).Should().BeTrue();

    [Theory]
    [InlineData("// task.Wait(); was the old shape")]
    [InlineData("/* .GetAwaiter().GetResult() */ var x = 1;")]
    [InlineData("var s = \"never .Wait() here\";")]
    [InlineData("var s = @\"a \"\" b.Wait()\";")]
    [InlineData("response.Type = BifrostMessageType.Result;")]
    [InlineData("await cache.GetValueAsync(p).WaitAsync(ct);")]
    public void BlockingWait_IgnoresCommentsLiteralsAndNonWaits(string code)
        => BlockingWait.IsMatch(CodeOnly(code)).Should().BeFalse();

    /// <summary>
    /// Strips comments and blanks literal contents, walking regular, verbatim and raw string
    /// literals and char literals so a <c>"//"</c> inside one neither opens a comment nor hides
    /// the code after it.
    /// </summary>
    private static string CodeOnly(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                output.Append(' ');
            }
            else if (c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"')
            {
                var end = source.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 3;
                output.Append("\"\"");
            }
            else if (c == '@' && next == '"')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '"' && (i + 1 >= source.Length || source[i + 1] != '"')))
                    i += source[i] == '"' ? 2 : 1;
                i++;
                output.Append("\"\"");
            }
            else if (c == '"' || c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != c && source[i] != '\n')
                    i += source[i] == '\\' ? 2 : 1;
                i++;
                output.Append(c).Append(c);
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }

    private static string LocateRepoRoot([CallerFilePath] string callerFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFilePath)!, "..", ".."));
}

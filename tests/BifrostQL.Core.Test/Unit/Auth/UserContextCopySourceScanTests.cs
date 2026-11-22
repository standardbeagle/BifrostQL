using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BifrostQL.Core.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

/// <summary>
/// A user context is copied only through <see cref="UserContexts.Copy"/>. A plain
/// <c>new Dictionary&lt;string, object?&gt;(userContext)</c> drops the HTTP context's
/// case-insensitive comparer, so a token carrying <c>Org_Id</c> under the key <c>org_id</c>
/// scoped some surfaces and refused others.
/// </summary>
public sealed class UserContextCopySourceScanTests
{
    // A dictionary built from an expression ending in "...Context" / "...context" (userContext,
    // context.UserContext, located.UserContext). The argument may wrap onto the next line.
    private static readonly Regex ContextCopy = new(
        @"new\s+Dictionary\s*<\s*string\s*,\s*object\s*\??\s*>\s*\(\s*[A-Za-z_][\w.]*[Cc]ontext\s*[,)]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HelperCall = new(@"\bUserContexts\s*\.\s*Copy\s*\(", RegexOptions.Compiled);

    [Fact]
    public void UserContexts_AreCopiedOnlyThroughTheCaseInsensitiveHelper()
    {
        var srcRoot = LocateSourceRoot();
        srcRoot.Should().NotBeNull();

        var files = Directory.EnumerateFiles(srcRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: Path.GetRelativePath(srcRoot!, path).Replace(Path.DirectorySeparatorChar, '/'),
                             Text: StripComments(File.ReadAllText(path))))
            .ToList();

        files.Sum(file => HelperCall.Matches(file.Text).Count).Should().BeGreaterThan(20,
            "the helper's call sites are the anchor; a scan that sees none is vacuous");

        files.SelectMany(file => ContextCopy.Matches(file.Text).Select(match => $"{file.Path}:{Line(file.Text, match.Index)}"))
            .Should().BeEmpty("a user-context copy must go through UserContexts.Copy to keep the case-insensitive comparer");
    }

    [Fact]
    public void Copy_KeepsCaseInsensitiveLookup_AndRefusesCaseVariantDuplicates()
    {
        var copy = UserContexts.Copy(new Dictionary<string, object?> { ["Org_Id"] = "acme" });
        copy.TryGetValue("org_id", out var tenant).Should().BeTrue();
        tenant.Should().Be("acme");

        var ambiguous = new Dictionary<string, object?> { ["org_id"] = "acme", ["Org_Id"] = "globex" };
        FluentActions.Invoking(() => UserContexts.Copy(ambiguous)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("var c = new Dictionary<string, object?>(userContext);")]
    [InlineData("var c = new Dictionary<string, object?>(\n    context.UserContext);")]
    [InlineData("var c = new Dictionary<string, object>(located.UserContext, StringComparer.Ordinal);")]
    [InlineData("var c = new Dictionary<string, object?>(userContext); var s = \"// not a comment\";")]
    public void Pattern_FlagsOrdinalCopies(string offender)
        => ContextCopy.IsMatch(StripComments(offender)).Should().BeTrue();

    [Theory]
    [InlineData("var c = UserContexts.Copy(userContext);")]
    [InlineData("var c = new Dictionary<string, object?>(context.Data, StringComparer.OrdinalIgnoreCase);")]
    [InlineData("// var c = new Dictionary<string, object?>(userContext);")]
    [InlineData("var s = \"//\"; var t = UserContexts.Copy(userContext);")]
    public void Pattern_IgnoresHelperCalls_DataCopies_AndComments(string source)
        => ContextCopy.IsMatch(StripComments(source)).Should().BeFalse();

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    // Strips // and /* */ comments while walking string and char literals, so a "//" inside a
    // literal does not hide the code after it.
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
                    output.Append(source[i] == '\n' ? '\n' : ' ');
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

    private static string? LocateSourceRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(callerFilePath)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "BifrostQL.Core")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

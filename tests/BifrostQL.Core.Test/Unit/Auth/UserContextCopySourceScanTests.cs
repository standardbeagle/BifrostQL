using System.Runtime.CompilerServices;
using System.Text;
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
///
/// <para>The scan is anchored on the copy APIs, never on what the copied variable is called: every
/// <c>Dictionary&lt;string, object?&gt;</c> built from another sequence (explicit
/// <c>new Dictionary&lt;..&gt;(x)</c>, a target-typed <c>new(x)</c> into such a declaration or
/// into a name the file declares with that type, and a key/value <c>ToDictionary</c>) must keep
/// <see cref="StringComparer.OrdinalIgnoreCase"/>. The ordinal copies that remain are row,
/// metadata and variables copies, listed by file and argument text with their count in
/// <see cref="NonUserContextCopies"/>; renaming one, or adding a second same-text copy to the same
/// file, surfaces it again for review.</para>
/// </summary>
public sealed class UserContextCopySourceScanTests
{
    private const string ObjectDictionary = @"Dictionary\s*<\s*string\s*,\s*object\s*\??\s*>";

    private static readonly Regex ExplicitCopy = new(@"\bnew\s+" + ObjectDictionary + @"\s*\(", RegexOptions.Compiled);

    private static readonly Regex TargetTypedCopy = new(
        @"\b" + ObjectDictionary + @"\s*\??\s+@?\w+\s*=\s*new\s*\(", RegexOptions.Compiled);

    // A name the file declares as an object dictionary (field, property, local or parameter).
    private static readonly Regex DictionaryDeclaration = new(
        @"\b" + ObjectDictionary + @"\s*\??\s+@?(\w+)", RegexOptions.Compiled);

    // A target-typed new assigned to a bare name; a copy when the file declares that name as an
    // object dictionary.
    private static readonly Regex TargetTypedAssignment = new(
        @"(?<![\w.])(?:this\s*\.\s*)?@?(\w+)\s*=\s*new\s*\(", RegexOptions.Compiled);

    private static readonly Regex ToDictionaryCall = new(@"\.\s*ToDictionary\s*\(", RegexOptions.Compiled);

    private static readonly Regex HelperCall = new(@"\bUserContexts\s*\.\s*Copy\s*\(", RegexOptions.Compiled);

    // A constructor argument that builds an empty dictionary (capacity or comparer), not a copy.
    private static readonly Regex NonCopyArgument = new(
        @"^(\d+|[\w.]+\.(Count|Length)|\w*[Cc]apacity|StringComparer\.\w+|\w*[Cc]omparer)$", RegexOptions.Compiled);

    private static readonly Regex KeySelector = new(@"^(\w+)\s*=>\s*\1\s*\.\s*Key$", RegexOptions.Compiled);
    // The value may be cast: kv => (object?)kv.Value.
    private static readonly Regex ValueSelector = new(
        @"^(\w+)\s*=>\s*(\(\s*[\w.?<>, ]+\s*\)\s*)?\1\s*\.\s*Value$", RegexOptions.Compiled);

    private const string CaseInsensitive = "StringComparer.OrdinalIgnoreCase";

    /// <summary>
    /// Ordinal copies of data that is not a user context: "path|arguments" (arguments with their
    /// whitespace collapsed) to the number of such copies in that file.
    /// </summary>
    private static readonly Dictionary<string, int> NonUserContextCopies = new(StringComparer.Ordinal)
    {
        // GraphQL input extensions, variables and error extensions.
        ["BifrostQL.Core/Resolvers/IBifrostContext.cs|_inner.InputExtensions"] = 1,
        ["BifrostQL.Server/GraphQLFrontend.cs|request.Variables"] = 1,
        ["BifrostQL.Server/GraphQLFrontend.cs|error.Extensions"] = 1,
        // Table metadata: the store keeps the source's own comparer.
        ["BifrostQL.Core/Model/MetadataDictionary.cs|plain, plain.Comparer"] = 1,
        ["BifrostQL.Core/Model/MetadataDictionary.cs|source"] = 1,
        ["BifrostQL.Core/Resolvers/MetaSchemaResolver.cs|kv => kv.Key, kv => kv.Value"] = 2,
        ["BifrostQL.Server/AppMetadataVisibility.cs|kv => kv.Key, kv => kv.Value, StringComparer.Ordinal"] = 1,
        // The operation-id fingerprint canonicalises write values ordinally on purpose.
        ["BifrostQL.Core/Modules/OperationId/OperationFingerprint.cs|kv => kv.Key, kv => kv.Value, StringComparer.Ordinal"] = 1,
        // Workflow step outputs.
        ["BifrostQL.Core/Workflows/IWorkflowRunner.cs|context.NamedOutputs"] = 1,
        // Mutation row data and SQL parameter bags.
        ["BifrostQL.Core/Modules/TenantMutationTransformer.cs|data"] = 2,
        ["BifrostQL.Core/Modules/ConcurrencyMutationTransformer.cs|data"] = 2,
        ["BifrostQL.Core/Modules/EnumValueMutationTransformer.cs|data"] = 1,
        ["BifrostQL.Core/Modules/SoftDeleteMutationTransformer.cs|data"] = 1,
        ["BifrostQL.Core/Modules/TreeSyncEngine.cs|scalarData"] = 1,
        ["BifrostQL.Core/Modules/Cdc/DeferredOutboxReleaseEngine.cs|values"] = 1,
        ["BifrostQL.Server/Ldap/LdapSearchExecutor.cs|kv => kv.Key, kv => kv.Value"] = 1,
    };

    [Fact]
    public void UserContexts_AreCopiedOnlyThroughTheCaseInsensitiveHelper()
    {
        var files = SourceFiles();

        files.Sum(file => HelperCall.Matches(file.Text).Count).Should().BeGreaterThan(20,
            "the helper's call sites are the anchor; a scan that sees none is vacuous");

        var copies = files.SelectMany(file => OrdinalCopies(file.Text)
            .Select(copy => (Key: $"{file.Path}|{copy.Arguments}", Where: $"{file.Path}:{copy.Line} ({copy.Arguments})")))
            .ToList();

        copies.Where(copy => !NonUserContextCopies.ContainsKey(copy.Key)).Select(copy => copy.Where)
            .Should().BeEmpty("a user-context copy must go through UserContexts.Copy to keep the case-insensitive comparer");
        copies.GroupBy(copy => copy.Key).ToDictionary(group => group.Key, group => group.Count())
            .Should().Equal(NonUserContextCopies,
                "a stale entry, or a second same-text copy in a listed file, would otherwise be admitted silently");
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
    [InlineData("var c = new Dictionary<string, object?>(frozen);")]
    [InlineData("var c = new Dictionary<string, object?>(\n    projected);")]
    [InlineData("var c = new Dictionary<string, object?>(projected")]
    [InlineData("var c = new Dictionary<string, object>(located.UserContext, StringComparer.Ordinal);")]
    [InlineData("Dictionary<string, object?> c = new(projected);")]
    [InlineData("private readonly Dictionary<string, object?>? _c = new(\n    projected);")]
    [InlineData("var c = projected.ToDictionary(kv => kv.Key, kv => kv.Value);")]
    [InlineData("var c = projected.ToDictionary(p => p.Key,\n    p => p.Value, StringComparer.Ordinal);")]
    [InlineData("var c = projected.ToDictionary();")]
    [InlineData("var c = new Dictionary<string, object?>(frozen); var s = \"// not a comment\";")]
    [InlineData("var s = @\"a \"\" // b\"; var c = new Dictionary<string, object?>(frozen);")]
    [InlineData("var s = \"\"\"\n  \" // raw\n  \"\"\"; var c = new Dictionary<string, object?>(frozen);")]
    [InlineData("var s = $\"{a} // {b}\"; var c = new Dictionary<string, object?>(frozen);")]
    [InlineData("var s = $\"{new Dictionary<string, object?>(frozen).Count}\";")]
    [InlineData("var s = $$\"\"\"{ {{new Dictionary<string, object?>(frozen).Count}} }\"\"\";")]
    [InlineData("var c = '\"'; var d = new Dictionary<string, object?>(frozen);")]
    [InlineData("var c = projected.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);")]
    [InlineData("var c = projected.ToDictionary(kv => kv.Key, kv => ( object ) kv.Value, StringComparer.Ordinal);")]
    [InlineData("private Dictionary<string, object?> _c = null!; void M() { _c = new(projected); }")]
    [InlineData("private Dictionary<string, object?>? _c; void M() { this._c =\n    new(projected); }")]
    public void Pattern_FlagsOrdinalCopies(string offender)
        => OrdinalCopies(StripComments(offender)).Should().NotBeEmpty();

    [Theory]
    [InlineData("var c = UserContexts.Copy(userContext);")]
    [InlineData("var c = new Dictionary<string, object?>(frozen, StringComparer.OrdinalIgnoreCase);")]
    [InlineData("var c = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);")]
    [InlineData("var c = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);")]
    [InlineData("var c = new Dictionary<string, object?>();")]
    [InlineData("Dictionary<string, object?> c = new();")]
    [InlineData("var c = projected.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);")]
    [InlineData("var c = rows.ToDictionary(r => r.Id, r => r.Name);")]
    [InlineData("var c = projected.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);")]
    [InlineData("private Dictionary<string, object?> _c = null!; void M() { _c = new(StringComparer.OrdinalIgnoreCase); }")]
    [InlineData("private List<string> _c = null!; void M() { _c = new(projected); }")]
    [InlineData("private Dictionary<string, object?> _c = null!; void M() { other._c = new(projected); }")]
    [InlineData("// var c = new Dictionary<string, object?>(userContext);")]
    [InlineData("/* new Dictionary<string, object?>(userContext) */ var x = 1;")]
    [InlineData("var s = \"new Dictionary<string, object?>(userContext)\";")]
    [InlineData("var s = @\"new Dictionary<string, object?>(userContext)\";")]
    [InlineData("var s = \"\"\"\n new Dictionary<string, object?>(userContext)\n \"\"\";")]
    [InlineData("var s = $\"{{new Dictionary<string, object?>(userContext)}}\";")]
    [InlineData("var s = \"//\"; var t = UserContexts.Copy(userContext);")]
    public void Pattern_IgnoresHelperCalls_CaseInsensitiveCopies_CommentsAndLiterals(string source)
        => OrdinalCopies(StripComments(source)).Should().BeEmpty();

    [Fact]
    public void StripComments_KeepsTheHelperAnchorAfterALiteralSlashPair()
        => HelperCall.IsMatch(StripComments("var s = @\"//\"; var t = UserContexts.Copy(x);")).Should().BeTrue();

    /// <summary>
    /// Copies of a sequence into a new object dictionary that do not end in a case-insensitive
    /// comparer. An argument list that never closes is an offender.
    /// </summary>
    private static IEnumerable<(int Line, string Arguments)> OrdinalCopies(string text)
    {
        var dictionaryNames = DictionaryDeclaration.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var assignments = TargetTypedAssignment.Matches(text).Where(m => dictionaryNames.Contains(m.Groups[1].Value));

        // A declaration's "x = new(" also matches the bare assignment; each "new(" counts once.
        foreach (var match in ExplicitCopy.Matches(text).Concat(TargetTypedCopy.Matches(text)).Concat(assignments)
                     .DistinctBy(m => m.Index + m.Length))
        {
            var arguments = Arguments(text, match.Index + match.Length);
            if (arguments is null)
                yield return (Line(text, match.Index), "<unclosed>");
            else if (arguments.Count > 0 && !NonCopyArgument.IsMatch(arguments[0]) && arguments[^1] != CaseInsensitive)
                yield return (Line(text, match.Index), string.Join(", ", arguments));
        }

        foreach (Match match in ToDictionaryCall.Matches(text))
        {
            var arguments = Arguments(text, match.Index + match.Length);
            if (arguments is null)
                yield return (Line(text, match.Index), "<unclosed>");
            else if (IsOrdinalPairCopy(arguments))
                yield return (Line(text, match.Index), arguments.Count == 0 ? "<pairs>" : string.Join(", ", arguments));
        }
    }

    // ToDictionary over key/value pairs: the parameterless and comparer-only overloads (which compile
    // only on a pair sequence) and the kv => kv.Key, kv => kv.Value projection.
    private static bool IsOrdinalPairCopy(List<string> arguments) => arguments.Count switch
    {
        0 => true,
        1 => !arguments[0].Contains("=>") && arguments[0] != CaseInsensitive,
        2 or 3 => KeySelector.IsMatch(arguments[0]) && ValueSelector.IsMatch(arguments[1])
                  && arguments[^1] != CaseInsensitive,
        _ => false,
    };

    /// <summary>The top-level arguments after an opening parenthesis, whitespace collapsed; null when unclosed.</summary>
    private static List<string>? Arguments(string text, int start)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ')')
            {
                if (depth == 0)
                {
                    var last = Collapse(current.ToString());
                    if (last.Length > 0 || arguments.Count > 0) arguments.Add(last);
                    return arguments;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(Collapse(current.ToString()));
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        return null;
    }

    private static string Collapse(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static List<(string Path, string Text)> SourceFiles()
    {
        var srcRoot = LocateSourceRoot();
        srcRoot.Should().NotBeNull();
        return Directory.EnumerateFiles(srcRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: Path.GetRelativePath(srcRoot!, path).Replace(Path.DirectorySeparatorChar, '/'),
                             Text: StripComments(File.ReadAllText(path))))
            .ToList();
    }

    /// <summary>
    /// Removes comments and blanks string and char literal contents (newlines kept, so line
    /// numbers hold), while keeping interpolation holes as code. Handles regular, verbatim, raw
    /// and interpolated strings, so neither a "//" nor a copy inside a literal changes the scan.
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        Code(source, ref i, output, insideHole: false);
        return output.ToString();
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    private static void Code(string s, ref int i, StringBuilder o, bool insideHole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && At(s, i + 1) == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && At(s, i + 1) == '/'))
                    o.Append(s[i++] == '\n' ? '\n' : ' ');
                i += 2;
                o.Append(' ');
            }
            else if (c == '\'')
            {
                o.Append(c);
                i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                {
                    if (s[i] == '\\') { o.Append(' '); i++; }
                    o.Append(' ');
                    i++;
                }
                if (i < s.Length) o.Append(s[i++]);
            }
            else if (IsStringStart(s, i))
            {
                StringLiteral(s, ref i, o);
            }
            else
            {
                if (insideHole)
                {
                    if (c == '{') depth++;
                    else if (c == '}' && depth-- == 0) return;
                }
                o.Append(c);
                i++;
            }
        }
    }

    private static bool IsStringStart(string s, int i)
    {
        var j = i;
        while (j < s.Length && s[j] is '$' or '@') j++;
        return At(s, j) == '"';
    }

    private static void StringLiteral(string s, ref int i, StringBuilder o)
    {
        var dollars = 0;
        var verbatim = false;
        while (s[i] is '$' or '@')
        {
            if (s[i] == '$') dollars++; else verbatim = true;
            o.Append(s[i++]);
        }

        var quotes = 0;
        while (At(s, i + quotes) == '"') quotes++;
        if (quotes == 2)
        {
            o.Append("\"\"");
            i += 2;
            return;
        }

        var raw = quotes >= 3;
        var delimiter = raw ? quotes : 1;
        o.Append('"', delimiter);
        i += delimiter;

        while (i < s.Length)
        {
            var c = s[i];
            if (raw && c == '"')
            {
                var run = 0;
                while (At(s, i + run) == '"') run++;
                if (run >= delimiter)
                {
                    o.Append(' ', run - delimiter).Append('"', delimiter);
                    i += run;
                    return;
                }
                o.Append(' ', run);
                i += run;
            }
            else if (!raw && c == '"')
            {
                if (verbatim && At(s, i + 1) == '"') { o.Append("  "); i += 2; continue; }
                o.Append('"');
                i++;
                return;
            }
            else if (!raw && !verbatim && c == '\\')
            {
                o.Append("  ");
                i += 2;
            }
            else if (dollars > 0 && c == '{')
            {
                var run = 0;
                while (At(s, i + run) == '{') run++;
                var opener = raw ? dollars : 1;
                if (!raw && run >= 2)
                {
                    // "{{" is a literal brace in a non-raw interpolated string.
                    o.Append(' ', run - run % 2);
                    i += run - run % 2;
                    continue;
                }
                if (run < opener)
                {
                    o.Append(' ', run);
                    i += run;
                    continue;
                }
                o.Append(' ', run - opener).Append('{');
                i += run;
                Code(s, ref i, o, insideHole: true);
                var closers = 0;
                while (closers < opener && At(s, i) == '}') { closers++; i++; }
                o.Append('}');
            }
            else
            {
                o.Append(c == '\n' ? '\n' : ' ');
                i++;
            }
        }
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

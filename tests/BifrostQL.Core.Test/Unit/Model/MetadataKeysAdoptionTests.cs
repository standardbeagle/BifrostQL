using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model
{
    /// <summary>
    /// Regression guard: every <c>GetMetadataValue</c>, <c>GetMetadataBool</c>,
    /// <c>GetMetadataInt</c> and <c>SetMetadata</c> call site under
    /// <c>src/BifrostQL.Core/</c> must use a named constant (typically from
    /// <c>MetadataKeys</c>) rather than a string literal as its first argument.
    ///
    /// The test scans the source tree at run time with a regex (no Roslyn
    /// dependency) so it is fast, deterministic, and fails loudly if a raw
    /// literal creeps back in.
    /// </summary>
    public class MetadataKeysAdoptionTests
    {
        // Capture the first argument up to the first ',' or ')'. The argument
        // is fragile only across newlines, which we do not currently tolerate
        // in the codebase. If a multi-line argument is ever added, this regex
        // can be widened.
        private static readonly Regex MetadataCallRegex = new(
            @"\b(GetMetadataValue|GetMetadataBool|GetMetadataInt|SetMetadata)\s*\(\s*([^,)]+?)\s*[,)]",
            RegexOptions.Compiled);

        // Allow-list for legitimate non-literal arguments that may LOOK like
        // literals (e.g. a string interpolation, verbatim quote, etc.). Keep
        // this empty unless you have a documented reason; every entry is a
        // hole in the regression guard.
        private static readonly HashSet<string> AllowedLiterals = new(StringComparer.Ordinal);

        [Fact]
        public void NoRawStringLiteralsInMetadataCalls()
        {
            var sourceRoot = LocateBifrostCoreSourceRoot();
            sourceRoot.Should().NotBeNull(
                "the BifrostQL.Core source directory must be locatable from the test assembly");

            var violations = new List<string>();

            foreach (var file in Directory.EnumerateFiles(sourceRoot!, "*.cs", SearchOption.AllDirectories))
            {
                // Skip generated and obj/bin output that may be mirrored in the source tree.
                var relative = Path.GetRelativePath(sourceRoot!, file).Replace('\\', '/');
                if (relative.StartsWith("obj/", StringComparison.Ordinal) ||
                    relative.StartsWith("bin/", StringComparison.Ordinal))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    // Skip comments quickly to avoid false positives in doc-comment examples.
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("///", StringComparison.Ordinal) ||
                        trimmed.StartsWith("*", StringComparison.Ordinal))
                        continue;

                    foreach (Match match in MetadataCallRegex.Matches(line))
                    {
                        var firstArg = match.Groups[2].Value.Trim();

                        if (!IsStringLiteral(firstArg))
                            continue;

                        if (AllowedLiterals.Contains(firstArg))
                            continue;

                        violations.Add(
                            $"{relative}:{i + 1}  {match.Groups[1].Value}({firstArg}) -> use a named constant from MetadataKeys");
                    }
                }
            }

            violations.Should().BeEmpty(
                "all metadata accessors must use MetadataKeys constants. Offenders:\n  " +
                string.Join("\n  ", violations));
        }

        /// <summary>
        /// Returns true when the captured argument starts and ends with a
        /// double quote, marking it as a C# string literal (regular or
        /// verbatim). Keep the check intentionally narrow so that constants,
        /// nameof(...) expressions, and variable references all pass.
        /// </summary>
        private static bool IsStringLiteral(string arg)
        {
            if (arg.Length < 2)
                return false;

            // Regular literal:  "..."
            if (arg[0] == '"' && arg[arg.Length - 1] == '"')
                return true;

            // Verbatim literal: @"..."
            if (arg.Length >= 3 && arg[0] == '@' && arg[1] == '"' && arg[arg.Length - 1] == '"')
                return true;

            // Interpolated literal: $"..." (still a literal, still forbidden as a key)
            if (arg.Length >= 3 && arg[0] == '$' && arg[1] == '"' && arg[arg.Length - 1] == '"')
                return true;

            return false;
        }

        /// <summary>
        /// Walks upward from this source file's compile-time location to find
        /// the <c>src/BifrostQL.Core</c> directory. Using
        /// <see cref="CallerFilePathAttribute"/> sidesteps the brittle
        /// <c>AppContext.BaseDirectory</c> -> repo-root traversal that breaks
        /// when tests run from non-standard output directories.
        /// </summary>
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
}

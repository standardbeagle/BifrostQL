using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap;

/// <summary>
/// Guards that no C# source file in this test project contains a literal NUL
/// (or other raw control) byte. A literal <c>0x00</c> in a source file makes git
/// classify the file as binary, so <c>git diff</c> / <c>git log -p</c> show
/// "Binary files differ" and review of the file goes blind. Control characters
/// belong in escape sequences (<c>\0</c>, <c>\u001B</c>), never as raw bytes.
/// </summary>
public class SourceControlByteHygieneTests
{
    [Fact]
    public void TestProjectSources_ContainNoLiteralControlBytes()
    {
        var testRoot = LocateServerTestRoot();
        testRoot.Should().NotBeNull(
            "the BifrostQL.Server.Test source directory must be locatable from the test assembly");

        var files = Directory.GetFiles(testRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty("the hygiene scan must actually match source files to guard anything");

        var violations = new List<string>();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                if (b < 0x20 && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                    violations.Add($"{Path.GetFileName(file)}: byte offset {i}: 0x{b:X2}");
            }
        }

        violations.Should().BeEmpty(
            "C# sources must not contain literal control bytes; use escape sequences (\\0, \\u001B) instead:\n"
            + string.Join("\n", violations));
    }

    private static string? LocateServerTestRoot([CallerFilePath] string callerFilePath = "")
    {
        if (string.IsNullOrEmpty(callerFilePath))
            return null;

        var dir = new DirectoryInfo(Path.GetDirectoryName(callerFilePath)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "BifrostQL.Server.Test");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}

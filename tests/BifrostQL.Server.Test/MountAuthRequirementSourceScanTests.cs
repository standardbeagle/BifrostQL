using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Source-scan guard for the epic's root-cause shape: the auth requirement of an
/// HTTP-mounted front door (binary WebSocket, protocol frontend, GraphQL mount) must be
/// derived in exactly ONE place — <c>MountAuthRequirement</c>. H8 and H14 each wrote a
/// private copy of the same derivation (<c>ResolveBinaryAuthRequirement</c>,
/// <c>ResolveFrontendAuthRequirement</c>); two copies of one security decision drift.
/// This fact goes RED the moment a second derivation reappears in
/// <c>src/BifrostQL.Server</c>.
/// </summary>
public class MountAuthRequirementSourceScanTests
{
    [Fact]
    public void ServerSources_ContainExactlyOneAuthRequirementDerivation()
    {
        var serverRoot = LocateServerSourceRoot();
        serverRoot.Should().NotBeNull(
            "the BifrostQL.Server source directory must be locatable from the test assembly");

        var files = Directory.GetFiles(serverRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty("the scan must actually match source files to guard anything");

        var perFile = files.ToDictionary(f => f, File.ReadAllText);
        var allText = string.Join("\n", perFile.Values);

        // The retired per-mount copies must stay retired.
        allText.Should().NotContain("ResolveBinaryAuthRequirement",
            "the binary mount's private derivation was folded into MountAuthRequirement; " +
            "reintroducing a second copy of one security decision is the drift this task removes");
        allText.Should().NotContain("ResolveFrontendAuthRequirement",
            "the frontend mount's private derivation was folded into MountAuthRequirement; " +
            "reintroducing a second copy of one security decision is the drift this task removes");

        // Exactly one file may DECLARE the derivation. A re-added copy under any new name
        // still carries the derivation's shape: a method returning the auth requirement
        // from the served endpoint's DisableAuth flag.
        var derivationShape = new Regex(@"static\s+bool\s+\w*Auth\w*Requirement\s*\(", RegexOptions.Compiled);
        var declaringFiles = files
            .Where(f => derivationShape.IsMatch(perFile[f]))
            .ToList();
        declaringFiles.Should().ContainSingle(
            "exactly one derivation of a mount's auth requirement may exist; " +
            "a second one is a second security decision that will drift. Offenders: "
            + string.Join(", ", declaringFiles.Select(Path.GetFileName)));

        // The shared helper must exist and be the declared derivation.
        var helperFiles = files
            .Where(f => perFile[f].Contains("static class MountAuthRequirement"))
            .ToList();
        helperFiles.Should().ContainSingle(
            "MountAuthRequirement is the one shared derivation the binary, frontend, and " +
            "GraphQL mounts all call");
    }

    private static string? LocateServerSourceRoot([CallerFilePath] string callerFilePath = "")
    {
        if (string.IsNullOrEmpty(callerFilePath))
            return null;

        // tests/BifrostQL.Server.Test/<this file> -> repo root -> src/BifrostQL.Server
        var testProjectDir = Path.GetDirectoryName(callerFilePath);
        if (testProjectDir == null)
            return null;
        var repoRoot = Path.GetFullPath(Path.Combine(testProjectDir, "..", ".."));
        var serverDir = Path.Combine(repoRoot, "src", "BifrostQL.Server");
        return Directory.Exists(serverDir) ? serverDir : null;
    }
}

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Source-scan guard for the pre-auth attempt-limiter consolidation (H11 follow-up): the
/// fixed-window arithmetic, the bounded key map and the two-axis accounting must live in
/// exactly ONE place — <c>ProtocolAuthAttemptLimiter</c> — with per-adapter SUBTYPES
/// (<c>LdapBindRateLimiter</c>, <c>RespAuthRateLimiter</c>, <c>PgAuthRateLimiter</c>) so no
/// two front doors share one budget. The window arithmetic was copied three times (LDAP,
/// then RESP as H11's deliberate near-copy, and pgwire's per-source half); three copies of
/// one security decision drift. Anchored on the DATA the copy must carry (the Window
/// record, the attempt-counter map, the sweep gate), never on names a copier may rename.
/// </summary>
public class ProtocolAuthAttemptLimiterSourceScanTests
{
    [Fact]
    public void ServerSources_ContainExactlyOneAuthAttemptWindowImplementation()
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

        // The shared base must exist and carry the window arithmetic itself.
        var baseFiles = files
            .Where(f => perFile[f].Contains("abstract class ProtocolAuthAttemptLimiter"))
            .ToList();
        baseFiles.Should().ContainSingle(
            "ProtocolAuthAttemptLimiter is the one shared implementation of the pre-auth " +
            "attempt window that every adapter's limiter derives from");
        var baseFile = baseFiles[0];
        var baseText = perFile[baseFile];

        // Positive anchors on the base: the DATA the arithmetic reads. Without these the
        // scan's negative half matches nothing and guards nothing.
        baseText.Should().Contain("record struct Window",
            "the scan's window-record anchor must match the base, or it matches nothing and guards nothing");
        baseText.Should().Contain("_windows",
            "the scan's attempt-counter-map anchor must match the base, or it matches nothing and guards nothing");

        // The window arithmetic's data shapes may live in the base ONLY. A re-added copy
        // under any new type or method name still carries the same attempt-bucket map. (The
        // sweep-gate name is deliberately NOT an anchor: LocalAuthEndpoint's LoginThrottle is a
        // failure lockout, not an attempt window, and shares the sweep idiom legitimately.)
        var windowShape = new Regex(@"record struct Window\s*\(", RegexOptions.Compiled);
        var bucketMap = new Regex(@"ConcurrentDictionary<string,\s*Window>", RegexOptions.Compiled);

        var offenders = files
            .Where(f => f != baseFile
                     && (windowShape.IsMatch(perFile[f])
                         || bucketMap.IsMatch(perFile[f])))
            .ToList();
        offenders.Should().BeEmpty(
            "exactly one implementation of the pre-auth attempt window may exist " +
            "(ProtocolAuthAttemptLimiter); a second copy is a second security decision that will drift. " +
            "Offenders: " + string.Join(", ", offenders.Select(Path.GetFileName)));

        // Each adapter keeps its own SUBTYPE (its own budget), never a shared base instance.
        var subtypeShape = new Regex(@":\s*ProtocolAuthAttemptLimiter\b", RegexOptions.Compiled);
        var subtypes = files.Where(f => subtypeShape.IsMatch(perFile[f])).ToList();
        subtypes.Should().BeEquivalentTo(
            new[]
            {
                Path.Combine(serverRoot!, "Ldap", "LdapBindRateLimiter.cs"),
                Path.Combine(serverRoot!, "Resp", "RespAuthRateLimiter.cs"),
                Path.Combine(serverRoot!, "Pgwire", "PgAuthRateLimiter.cs"),
            },
            "LDAP, RESP and pgwire each keep a per-adapter subtype with its own budget — " +
            "registering the shared base directly would let two front doors consume one budget");
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

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// One pre-auth deadline implementation for every protocol front door.
///
/// <para><c>.claude/rules/protocol-adapter-security.md</c> invariant 15 states ONE rule: the
/// admission slot is taken at accept, so a peer that never authenticates must not hold it past
/// the configured pre-auth timeout; only a CREDENTIALED action retires that deadline, every free
/// action (anonymous bind, unauthenticated ping, StartTLS, version negotiation, cancel request)
/// leaves an armed deadline untouched, and the timer is ONE owned
/// <see cref="CancellationTokenSource"/> whose lifetime is the connection's, driven by a
/// <see cref="TimeProvider"/>. pgwire, RESP and LDAP each shipped their own arm/retire/re-arm
/// code for that rule — three copies of one fail-closed decision, on two different clock seams.
/// H11(b)(c) and M17 were drift between them, and each drift failed OPEN (a deadline that a free
/// action renews is equivalent to no deadline at all).</para>
///
/// <para><b>Anchor.</b> The scan is anchored on the PRE-AUTH TIMEOUT OPTION — the datum an
/// arming copy cannot avoid reading, whatever it renames its locals or its methods to. A copier
/// is free to rename <c>sessionDeadline</c>, to hoist <c>_options.AuthenticationTimeout</c> into
/// a local, to wrap the arithmetic in a helper, or to swap the timer mechanism; it is not free
/// to arm a CONFIGURED deadline without reading the configured value. Anchoring on an identifier
/// or on an operand shape is what let the admission scan's first anchor pass a hoisted-increment
/// mutant (commit 39b419b0), so this one anchors on the data read.</para>
///
/// <para><b>Non-vacuity.</b> Per <c>.claude/rules/regression-test-non-vacuous.md</c> the scan
/// asserts the POSITIVE hit inside the allowlisted home and fails when that count is zero — "no
/// offender matched" and "the pattern matched nothing at all" are otherwise the same GREEN. It
/// reads whole FILES rather than lines, so a wrapped C# member access cannot read as a miss.</para>
/// </summary>
public class ProtocolPreAuthDeadlineUnificationTests
{
    /// <summary>
    /// A read of a configured pre-auth timeout. Whitespace is allowed after the dot because a
    /// member access wraps freely across lines in C#; <c>\b</c> after the name keeps
    /// <c>TlsHandshakeTimeout</c> (the TLS handshake's own deadline, a different bound) out —
    /// there is no word boundary before its <c>Handshake</c>, so only a literal <c>.</c> or
    /// <c>.</c>-plus-whitespace immediately ahead of the name matches.
    /// </summary>
    private static readonly Regex PreAuthTimeoutRead = new(
        @"\.\s*(AuthenticationTimeout|HandshakeTimeout)\b",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void ServerSources_ReadThePreAuthTimeoutOnlyInTheSharedSessionHost()
    {
        var serverRoot = LocateServerSourceRoot();
        serverRoot.Should().NotBeNull("the BifrostQL.Server source directory must be locatable");

        var files = Directory.GetFiles(serverRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty("the scan must match source files to guard anything");

        // Whole-file text, not lines: `_options\n    .AuthenticationTimeout` is one read that a
        // per-line scan sees as two unrelated fragments.
        var perFile = files.ToDictionary(f => f, File.ReadAllText);

        var home = files.SingleOrDefault(f => Path.GetFileName(f) == "ProtocolSessionHost.cs");
        home.Should().NotBeNull(
            "the shared pre-auth deadline lives in ProtocolSessionHost.cs; without it there is no home "
            + "and every adapter is free to arm its own");

        PreAuthTimeoutRead.Matches(perFile[home!]).Count.Should().BeGreaterThanOrEqualTo(3,
            "the home must read the pre-auth timeout of all three raw-wire front doors (pgwire, RESP, "
            + "LDAP). A lower count means the anchor has drifted away from real code, or an adapter "
            + "stopped routing through the host — either way this fact would guard nothing");

        // Reads that do not ARM anything. Each entry names WHY; adding one is a review decision,
        // not a way past this fact.
        var nonArmingReads = new HashSet<string>(StringComparer.Ordinal)
        {
            "PgWireOptions.cs",    // declares HandshakeTimeout
            "RespWireOptions.cs",  // declares AuthenticationTimeout
            "LdapWireOptions.cs",  // declares AuthenticationTimeout
            "LdapWireAdapter.cs",  // startup validation: refuses a non-positive timeout before any listener binds
        };

        var offenders = files
            .Where(f => f != home
                     && !nonArmingReads.Contains(Path.GetFileName(f))
                     && PreAuthTimeoutRead.IsMatch(perFile[f]))
            .ToList();

        offenders.Should().BeEmpty(
            "one pre-auth deadline implementation, shared: a private copy is a second set of "
            + "semantics for invariant 15's one rule, and every historical drift between the copies "
            + "failed OPEN. Offenders: "
            + string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    private static string? LocateServerSourceRoot([CallerFilePath] string callerFilePath = "")
    {
        if (string.IsNullOrEmpty(callerFilePath)) return null;
        var testProjectDir = Path.GetDirectoryName(callerFilePath);
        if (testProjectDir == null) return null;
        var repoRoot = Path.GetFullPath(Path.Combine(testProjectDir, "..", ".."));
        var serverDir = Path.Combine(repoRoot, "src", "BifrostQL.Server");
        return Directory.Exists(serverDir) ? serverDir : null;
    }
}

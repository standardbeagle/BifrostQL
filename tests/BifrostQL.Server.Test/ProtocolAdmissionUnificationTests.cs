using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// One admission counter implementation for every protocol front door.
///
/// <para>AGENTS.md requires each adapter to take its slot at ACCEPT and to own a DISTINCT
/// counter subtype, and <c>ProtocolConnectionLimiter</c> is the single implementation of that
/// contract. LDAP shipped a private second copy of the same lock-free CAS loop
/// (<c>LdapBoundedCounter</c>) instead, so the shared type's semantics and the LDAP front
/// door's could drift independently — the drift shape that produced H11(b)(c) and M17 on the
/// deadline half of the same lifecycle.</para>
///
/// <para>The source scan below is written to be non-vacuous: it asserts the POSITIVE hit inside
/// the allowlisted home (the shared file must still contain the loop) as well as the absence of
/// offenders, and it reads whole files rather than lines so a wrapped call cannot slip past.</para>
/// </summary>
public class ProtocolAdmissionUnificationTests
{
    [Fact]
    public void LdapFrontDoor_AdmitsThroughItsOwnProtocolConnectionLimiterSubtype()
    {
        var services = new ServiceCollection();
        services.AddBifrostLdap(o => o.Port = 3899);
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<LdapConnectionHandler>();
        var counter = AdmissionCounterOf(handler);

        counter.Should().BeAssignableTo<ProtocolConnectionLimiter>(
            "every front door admits through the one shared admission implementation, "
            + "never a private second copy of the same CAS loop");
        counter.Should().BeOfType<LdapConnectionLimiter>(
            "each adapter owns a DISTINCT subtype so two front doors can never share one budget");
    }

    [Fact]
    public void LdapAndLdaps_ShareTheSameAdmissionCounterInstance()
    {
        var services = new ServiceCollection();
        services.AddBifrostLdap(o =>
        {
            o.Port = 3899;
            o.LdapsPort = 3636;
            o.ServerCertificate = BifrostQL.Server.Test.Ldap.LdapTestCertificate.Instance;
        });
        using var provider = services.BuildServiceProvider();

        var cleartext = AdmissionCounterOf(provider.GetRequiredService<LdapConnectionHandler>());
        var confidential = AdmissionCounterOf(provider.GetRequiredService<LdapsConnectionHandler>());

        confidential.Should().BeSameAs(cleartext,
            "MaxConnections is this front door's TOTAL ceiling; opening the second port must not double it");
    }

    [Fact]
    public void AdmissionCounterSubtypes_AreDistinctPerFrontDoor()
    {
        var subtypes = typeof(ProtocolConnectionLimiter).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ProtocolConnectionLimiter)))
            .ToList();

        subtypes.Should().Contain(typeof(LdapConnectionLimiter));
        subtypes.Should().OnlyContain(t => t.IsSealed,
            "a shared, non-sealed subtype would let two adapters register the same service type "
            + "and silently draw on one counter");
    }

    [Fact]
    public void ServerSources_DeclareTheAdmissionCasLoopOnlyInTheSharedLimiter()
    {
        var serverRoot = LocateServerSourceRoot();
        serverRoot.Should().NotBeNull("the BifrostQL.Server source directory must be locatable");

        var files = Directory.GetFiles(serverRoot!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty("the scan must match source files to guard anything");

        // Whole-file text, not lines: a C# argument list wraps freely, so a per-line scan sees a
        // truncated call and reads a real offender as a miss.
        var perFile = files.ToDictionary(f => f, File.ReadAllText);

        // Anchored on what a copy cannot avoid WRITING — the compare-and-swap that reserves a slot
        // — not on an identifier the copier is free to rename.
        var casShape = new Regex(
            @"Interlocked\s*\.\s*CompareExchange\s*\(\s*ref\s+\w+\s*,\s*\w+\s*\+\s*1\s*,",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var home = files.Single(f => Path.GetFileName(f) == "ProtocolConnectionLimiter.cs");
        casShape.Matches(perFile[home]).Count.Should().BeGreaterThan(0,
            "the scan's anchor must match the allowlisted home, or the pattern has drifted away "
            + "from real code and this fact guards nothing");

        var offenders = files.Where(f => f != home && casShape.IsMatch(perFile[f])).ToList();
        offenders.Should().BeEmpty(
            "one admission implementation, shared: a private copy of the CAS loop is a second set "
            + "of semantics that drifts. Offenders: "
            + string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    private static object AdmissionCounterOf(object handler)
    {
        var field = handler.GetType()
            .GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"{handler.GetType().Name} must hold its admission counter in _connections");
        return field!.GetValue(handler)!;
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

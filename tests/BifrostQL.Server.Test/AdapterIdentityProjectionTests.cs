using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// One fail-closed identity projection for every raw-wire front door.
///
/// <para>pgwire, RESP and LDAP each carried a private <c>TryProjectIdentity</c> — three copies of
/// one security decision: a verified credential grants a Bifrost identity only if the candidate
/// principal projects to a NON-EMPTY user context, because an empty context is not a refusal
/// (protocol-adapter-security invariant 12) and the executor would serve it. Three copies is three
/// places for that rule to drift, and every drift fails OPEN.</para>
///
/// <para>The scan is anchored on the read no copy can avoid — <c>CreateUserContext</c> on the
/// shared auth factory — rather than on the method name a copier is free to change, and asserts
/// the POSITIVE hit inside the allowlisted home so a drifted pattern cannot read as coverage.</para>
/// </summary>
public class AdapterIdentityProjectionTests
{
    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Project_AnIdentityBearingPrincipal_YieldsItsUserContext()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") }, "test"));

        var projected = AdapterIdentityProjection.TryProject(
            BifrostAuthContextFactory.Instance, EmptyServices(), principal,
            NullLogger.Instance, "test", out var userContext);

        projected.Should().BeTrue();
        userContext.Should().NotBeEmpty();
    }

    [Fact]
    public void Project_APrincipalThatYieldsNoIdentity_IsRefused()
    {
        // An unauthenticated / claim-less principal projects to nothing. An empty user context is
        // NOT a refusal — it only gates tables that declare tenant metadata — so the projection
        // itself must refuse rather than hand one back.
        var projected = AdapterIdentityProjection.TryProject(
            BifrostAuthContextFactory.Instance, EmptyServices(), new ClaimsPrincipal(new ClaimsIdentity()),
            NullLogger.Instance, "test", out var userContext);

        projected.Should().BeFalse();
        userContext.Should().BeEmpty();
    }

    [Fact]
    public void Project_AFaultingFactory_IsRefused_NotDegradedToAnonymous()
    {
        var projected = AdapterIdentityProjection.TryProject(
            new ThrowingAuthContextFactory(), EmptyServices(),
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u") }, "t")),
            NullLogger.Instance, "test", out var userContext);

        projected.Should().BeFalse("a subject-less principal or an unmapped issuer fails closed");
        userContext.Should().BeEmpty();
    }

    [Fact]
    public void RawWireAdapters_ProjectIdentityOnlyThroughTheSharedSeam()
    {
        var serverRoot = LocateServerSourceRoot();
        serverRoot.Should().NotBeNull("the BifrostQL.Server source directory must be locatable");

        var adapterDirs = new[] { "Ldap", "Pgwire", "Resp" }
            .Select(d => Path.Combine(serverRoot!, d))
            .ToList();
        adapterDirs.Should().OnlyContain(d => Directory.Exists(d));

        var files = adapterDirs
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty("the scan must match source files to guard anything");

        // Whole-file text: a C# argument list wraps freely, so a per-line scan reads a wrapped
        // call as a miss and a real offender passes.
        var perFile = files.ToDictionary(f => f, File.ReadAllText);
        var projectionCall = new Regex(@"CreateUserContext\s*\(", RegexOptions.Compiled | RegexOptions.Singleline);

        var home = Path.Combine(serverRoot!, "ProtocolAdapter.cs");
        projectionCall.Matches(File.ReadAllText(home)).Count.Should().BeGreaterThan(0,
            "the scan's anchor must match the allowlisted home (AdapterIdentityProjection), or the "
            + "pattern has drifted away from real code and this fact guards nothing");

        var offenders = files.Where(f => projectionCall.IsMatch(perFile[f])).ToList();
        offenders.Should().BeEmpty(
            "pgwire, RESP and LDAP project identity through AdapterIdentityProjection; a private "
            + "copy is a second fail-closed decision that drifts. Offenders: "
            + string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    private sealed class ThrowingAuthContextFactory : IBifrostAuthContextFactory
    {
        public IDictionary<string, object?> CreateUserContext(HttpContext context)
            => throw new InvalidOperationException("unmapped issuer");
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

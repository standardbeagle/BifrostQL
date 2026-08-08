using BifrostQL.UI.Web;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// The vault connect endpoint appends a remedy to one specific failure: the server's
/// TLS certificate being rejected. That is the failure a vault entry which used to
/// connect will now hit, since certificate validation is enforced, so the detection
/// has to survive the nesting the DB drivers wrap it in — a bare "SSL Provider,
/// error: 0" with no guidance leaves the user with no idea which setting to change.
/// </summary>
public sealed class VaultConnectDiagnosticsTests
{
    [Fact]
    public void CertificateFailure_IsDetectedThroughDriverNesting()
    {
        // Shape SqlClient produces: a login-process failure wrapping the SSL detail.
        var inner = new InvalidOperationException(
            "The certificate chain was issued by an authority that is not trusted.");
        var ex = new InvalidOperationException(
            "A connection was successfully established with the server, but then an error "
            + "occurred during the login process. (provider: SSL Provider, error: 0)",
            inner);

        VaultEndpoints.IsCertificateValidationFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void UnrelatedFailure_GetsNoCertificateHint()
    {
        var ex = new InvalidOperationException(
            "Login failed for user 'sa'.",
            new TimeoutException("The wait operation timed out."));

        VaultEndpoints.IsCertificateValidationFailure(ex).Should().BeFalse();
    }
}

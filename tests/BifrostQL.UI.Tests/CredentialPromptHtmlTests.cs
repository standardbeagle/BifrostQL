using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Regression guards for the security controls baked into the credential-prompt
/// document. There is no current bug — these pin the invariants so a future edit
/// can't silently weaken them (e.g. drop the CSP, allow a form POST, or render the
/// host-supplied vault name via innerHTML and reintroduce XSS).
/// </summary>
public sealed class CredentialPromptHtmlTests
{
    private static readonly string Html = CredentialPromptHtml.Html;

    [Fact]
    public void EnforcesLockedDownContentSecurityPolicy()
    {
        Html.Should().Contain("Content-Security-Policy");
        Html.Should().Contain("default-src 'none'");
        Html.Should().Contain("form-action 'none'", "credentials must not be POSTable anywhere");
    }

    [Fact]
    public void VaultName_IsRenderedViaTextContent_NotInnerHtml()
    {
        // The vault name is host-supplied; rendering it through textContent keeps it
        // inert. innerHTML on that value would be an XSS sink.
        Html.Should().Contain("vaultNameEl.textContent =");
        Html.Should().NotContain("vaultNameEl.innerHTML");
    }

    [Fact]
    public void PasswordField_DisablesCredentialManagerSave()
    {
        Html.Should().Contain("type=\"password\"");
        Html.Should().Contain("autocomplete=\"new-password\"");
    }

    [Fact]
    public void CredentialsLeaveOnlyViaNativeBridge()
    {
        // The only egress is window.external.sendMessage; there must be no <form>
        // that could submit the values elsewhere.
        Html.Should().Contain("window.external.sendMessage");
        Html.Should().NotContain("<form");
    }

    [Fact]
    public void ClearsInputsAfterSubmitOrCancel()
    {
        Html.Should().Contain("zeroInputs");
    }

    [Fact]
    public void SwallowsDevToolsShortcuts()
    {
        Html.Should().Contain("F12");
    }
}

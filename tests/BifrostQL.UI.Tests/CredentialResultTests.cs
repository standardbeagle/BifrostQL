using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Pure-logic tests for <see cref="CredentialResult"/>. The record carries a
/// sensitive <c>Password</c> field, so these tests exist primarily to pin
/// the redaction behavior of <c>ToString()</c> — if the override is ever
/// deleted and the default record <c>ToString()</c> comes back, these
/// tests fail loudly and prevent a password-leak regression.
/// </summary>
public sealed class CredentialResultTests
{
    [Fact]
    public void Saved_SetsIsSavedAndBothFields()
    {
        // Arrange / Act
        var result = CredentialResult.Saved("alice", "hunter2");

        // Assert
        result.IsSaved.Should().BeTrue();
        result.Username.Should().Be("alice");
        result.Password.Should().Be("hunter2");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Cancelled_IsNotSavedAndHasNoFields()
    {
        var result = CredentialResult.Cancelled();

        result.IsSaved.Should().BeFalse();
        result.Username.Should().BeNull();
        result.Password.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failed_CarriesErrorMessage()
    {
        var result = CredentialResult.Failed("bridge timed out");

        result.IsSaved.Should().BeFalse();
        result.Username.Should().BeNull();
        result.Password.Should().BeNull();
        result.ErrorMessage.Should().Be("bridge timed out");
    }

    [Fact]
    public void ToString_OnSaved_DoesNotContainPasswordValue()
    {
        // Arrange — a password that would be obvious in any log output
        const string sensitivePassword = "P@ssw0rd!sensitive-marker-xyz";
        var result = CredentialResult.Saved("alice", sensitivePassword);

        // Act
        var rendered = result.ToString();

        // Assert — username is allowed in logs, password is not.
        rendered.Should().NotContain(sensitivePassword);
        rendered.Should().Contain("<redacted>");
        rendered.Should().Contain("alice");
        rendered.Should().Contain("IsSaved = true");
    }

    [Fact]
    public void ToString_OnCancelled_DoesNotLeakNullFields()
    {
        var result = CredentialResult.Cancelled();

        var rendered = result.ToString();

        rendered.Should().Contain("IsSaved = false");
        rendered.Should().NotContain("<redacted>"); // no password to redact
    }

    [Fact]
    public void ToString_OnFailed_IncludesErrorMessage()
    {
        var result = CredentialResult.Failed("vault path unwritable");

        var rendered = result.ToString();

        rendered.Should().Contain("IsSaved = false");
        rendered.Should().Contain("vault path unwritable");
    }

    [Fact]
    public void RecordEquality_TwoSavedWithSameCreds_AreEqual()
    {
        // Belt-and-braces: make sure record equality still works normally
        // despite the custom ToString override (overriding ToString should
        // not affect the compiler-generated Equals).
        var a = CredentialResult.Saved("alice", "hunter2");
        var b = CredentialResult.Saved("alice", "hunter2");

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void RecordEquality_DifferentPasswords_AreNotEqual()
    {
        var a = CredentialResult.Saved("alice", "hunter2");
        var b = CredentialResult.Saved("alice", "different");

        a.Should().NotBe(b);
    }

    [Fact]
    public void CredentialPromptHtml_ContainsCspAndFormElements()
    {
        // Smoke-test for the embedded HTML doc. The CSP header is the main
        // security control for the child window; if it drifts we want the
        // test suite to catch it before the window ships.
        var html = CredentialPromptHtml.Html;

        // Content Security Policy must lock down default-src to nothing.
        html.Should().Contain("Content-Security-Policy");
        html.Should().Contain("default-src 'none'");
        html.Should().Contain("form-action 'none'");

        // Form must have the ids the C# side expects
        html.Should().Contain("id=\"username\"");
        html.Should().Contain("id=\"password\"");
        html.Should().Contain("id=\"save\"");
        html.Should().Contain("id=\"cancel\"");
        html.Should().Contain("id=\"vault-name\"");

        // Message wire format matches NativeBridgeHost { id, kind, payload }
        html.Should().Contain("credential-save");
        html.Should().Contain("credential-cancel");

        // No external resources referenced (CSP would block them anyway,
        // but let's make sure we don't even mention them).
        html.Should().NotContain("http://");
        html.Should().NotContain("https://");
    }
}

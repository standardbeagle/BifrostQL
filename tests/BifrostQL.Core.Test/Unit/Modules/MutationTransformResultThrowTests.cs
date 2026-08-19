using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Pins the contract of <see cref="MutationTransformResult.ThrowIfDenied"/>, the one
/// method every mutation execution path funnels its "non-empty Errors → throw" step
/// through. The property under test is the one three hand-rolled copies of this throw
/// (batch pipeline, file-delete, file-upload) violated: a denial's ErrorCode must reach
/// the wire so every transport funnel maps the condition, not just the op class
/// (.claude/rules/protocol-adapter-security.md rule 10).
/// </summary>
public sealed class MutationTransformResultThrowTests
{
    private static MutationTransformResult Result(string[] errors, string? errorCode) => new()
    {
        MutationType = MutationType.Update,
        Data = new Dictionary<string, object?>(),
        Errors = errors,
        ErrorCode = errorCode,
    };

    [Fact]
    public void ThrowIfDenied_NoErrors_DoesNotThrow()
    {
        var act = () => Result(System.Array.Empty<string>(), null).ThrowIfDenied();

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfDenied_AccessDenial_CarriesTheErrorCodeOntoTheError()
    {
        // The exact property the hand-rolled throws dropped: an access-denial ErrorCode
        // must survive onto the thrown BifrostExecutionError.
        var result = Result(new[] { "Access denied by authorization policy." }, BifrostExecutionError.AccessDeniedCode);

        var thrown = result.Invoking(r => r.ThrowIfDenied())
            .Should().Throw<BifrostExecutionError>()
            .WithMessage("*Access denied*");
        thrown.Which.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public void ThrowIfDenied_NonAuthorizationError_LeavesTheCodeNull()
    {
        // A validation/enum/concurrency rejection carries no access-denied code, so it
        // must stay a generic fault — the helper must not invent a code.
        var result = Result(new[] { "value out of range" }, null);

        result.Invoking(r => r.ThrowIfDenied())
            .Should().Throw<BifrostExecutionError>()
            .Which.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void ThrowIfDenied_MultipleErrors_JoinsThemWithSemicolons()
    {
        var result = Result(new[] { "first", "second" }, null);

        result.Invoking(r => r.ThrowIfDenied())
            .Should().Throw<BifrostExecutionError>()
            .WithMessage("first; second");
    }
}

using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.OperationId;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// The owner an operation id is recorded under: the caller's tenant claim (the model's tenant
/// context key) and user id, each empty when absent. A claim that is not a single value is
/// refused rather than recorded under a guessed owner.
/// </summary>
public sealed class OperationIdOwnerTests
{
    private static IDbModel Model(string? tenantContextKey = null) => new DbModel
    {
        Tables = [],
        Metadata = tenantContextKey is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { [MetadataKeys.Security.TenantContextKey] = tenantContextKey },
    };

    [Fact]
    public void ScalarClaims_AreTheOwner()
    {
        var owner = OperationIdExecution.OwnerOf(Model(), new Dictionary<string, object?>
        {
            ["tenant_id"] = 7,
            ["user_id"] = "u-1",
        });

        owner.Should().Be(new OperationOwner("7", "u-1"));
    }

    [Fact]
    public void ConfiguredTenantKey_IsTheOneRead()
    {
        var owner = OperationIdExecution.OwnerOf(Model(tenantContextKey: "org_id"), new Dictionary<string, object?>
        {
            ["tenant_id"] = "ignored",
            ["org_id"] = "org-3",
        });

        owner.Should().Be(new OperationOwner("org-3", ""));
    }

    [Fact]
    public void NoClaims_AreAnEmptyOwner()
    {
        OperationIdExecution.OwnerOf(Model(), new Dictionary<string, object?>())
            .Should().Be(new OperationOwner("", ""));
    }

    [Theory]
    [InlineData("tenant_id", "tenant")]
    [InlineData("user_id", "user id")]
    public void MultiValuedClaim_IsRefused(string key, string claimName)
    {
        var userContext = new Dictionary<string, object?>
        {
            ["tenant_id"] = "t-1",
            ["user_id"] = "u-1",
        };
        userContext[key] = new[] { "a", "b" };

        var act = () => OperationIdExecution.OwnerOf(Model(), userContext);

        act.Should().Throw<BifrostExecutionError>()
            .Where(e => e.ErrorCode == BifrostExecutionError.AccessDeniedCode)
            .WithMessage($"The caller's {claimName} is not a single value, so the operation id cannot be recorded.");
    }
}

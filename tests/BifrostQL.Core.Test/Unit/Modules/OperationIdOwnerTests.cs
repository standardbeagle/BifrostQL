using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.OperationId;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// The owner an operation id is recorded under: the caller's tenant claim (the model's tenant
/// context key), empty when absent, and user id. A caller without a user id is refused, so no
/// two callers share an id space; a claim that is not a single value is refused rather than
/// recorded under a guessed owner.
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
            ["user_id"] = "u-2",
        });

        owner.Should().Be(new OperationOwner("org-3", "u-2"));
    }

    [Fact]
    public void UserIdWithoutTenant_IsAnOwnerWithAnEmptyTenant()
    {
        OperationIdExecution.OwnerOf(Model(), new Dictionary<string, object?> { ["user_id"] = "u-3" })
            .Should().Be(new OperationOwner("", "u-3"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("t-1", null)]
    [InlineData("t-1", "")]
    [InlineData("t-1", "  ")]
    public void CallerWithoutUserId_IsRefused(string? tenant, string? userId)
    {
        var userContext = new Dictionary<string, object?>();
        if (tenant is not null) userContext["tenant_id"] = tenant;
        if (userId is not null) userContext["user_id"] = userId;

        var act = () => OperationIdExecution.OwnerOf(Model(), userContext);

        act.Should().Throw<BifrostExecutionError>()
            .Where(e => e.ErrorCode == "OPERATION_ID_ANONYMOUS")
            .WithMessage("An operation id needs a caller with a user id.");
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

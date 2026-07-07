using System.Reflection;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.StoredProcedure;

/// <summary>
/// Verifies the fix for the HIGH finding: <see cref="StoredProcedureResolver"/>
/// had no authorization gate at all — unlike <c>_rawQuery</c>
/// (<c>raw-sql-role</c>) and <c>_table</c> (<c>generic-table-role</c>), any
/// exposed stored procedure was callable by any caller with zero role check.
///
/// The fix mirrors the <c>_rawQuery</c>/<c>_table</c> pattern via a private
/// <c>ValidateAuthorization</c> gate (exercised here through reflection, same
/// as other resolver tests reach non-public members): when the model
/// configures <see cref="StoredProcedureResolver.RoleMetadataKey"/>
/// (<c>stored-procedure-role</c>), the caller must be authenticated and hold
/// that role. Filter transformers cannot rewrite an arbitrary proc body, so
/// this is an auth gate only, not row-level scoping.
///
/// <see cref="StoredProcedureResolver.RoleMetadataKey"/> is a local literal
/// (not <c>MetadataKeys.StoredProcedures</c>) because that class, and the
/// model-metadata validation allow-list, are owned by another workstream —
/// promoting the key and wiring it into the allow-list is a required
/// follow-up documented on the constant itself.
/// </summary>
public sealed class StoredProcedureAuthorizationTests
{
    private static readonly MethodInfo ValidateAuthorizationMethod = typeof(StoredProcedureResolver)
        .GetMethod("ValidateAuthorization", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void InvokeValidateAuthorization(IDictionary<string, object?> userContext, IDbModel model)
    {
        try
        {
            ValidateAuthorizationMethod.Invoke(null, new object[] { userContext, model });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void ValidateAuthorization_NoRoleConfigured_DoesNotThrow()
    {
        // No stored-procedure-role metadata: mirrors pre-fix (open) behavior.
        // This documents the current fallback, not an endorsement — see the
        // follow-up note on RoleMetadataKey about making the gate unconditional
        // once MetadataKeys.StoredProcedures.Role exists.
        var model = DbModelTestFixture.Create().Build();

        var act = () => InvokeValidateAuthorization(new Dictionary<string, object?>(), model);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateAuthorization_RoleConfigured_NoUser_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(StoredProcedureResolver.RoleMetadataKey, "sp-caller")
            .Build();

        var act = () => InvokeValidateAuthorization(new Dictionary<string, object?>(), model);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*Authentication required*");
    }

    [Fact]
    public void ValidateAuthorization_RoleConfigured_NonClaimsPrincipalUser_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(StoredProcedureResolver.RoleMetadataKey, "sp-caller")
            .Build();
        var userContext = new Dictionary<string, object?> { ["user"] = "not-a-principal" };

        var act = () => InvokeValidateAuthorization(userContext, model);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*Authentication required*");
    }

    [Fact]
    public void ValidateAuthorization_RoleConfigured_MissingRole_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(StoredProcedureResolver.RoleMetadataKey, "sp-caller")
            .Build();
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "testuser") }, "test");
        var principal = new ClaimsPrincipal(identity);
        var userContext = new Dictionary<string, object?> { ["user"] = principal };

        var act = () => InvokeValidateAuthorization(userContext, model);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*does not have the required role*");
    }

    [Fact]
    public void ValidateAuthorization_RoleConfigured_WithRoleClaim_Succeeds()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(StoredProcedureResolver.RoleMetadataKey, "sp-caller")
            .Build();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim("role", "sp-caller"),
        }, "test");
        var principal = new ClaimsPrincipal(identity);
        var userContext = new Dictionary<string, object?> { ["user"] = principal };

        var act = () => InvokeValidateAuthorization(userContext, model);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateAuthorization_RoleConfigured_WithRoleViaIsInRole_Succeeds()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(StoredProcedureResolver.RoleMetadataKey, "sp-caller")
            .Build();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim(ClaimTypes.Role, "sp-caller"),
        }, "test");
        var principal = new ClaimsPrincipal(identity);
        var userContext = new Dictionary<string, object?> { ["user"] = principal };

        var act = () => InvokeValidateAuthorization(userContext, model);

        act.Should().NotThrow();
    }
}

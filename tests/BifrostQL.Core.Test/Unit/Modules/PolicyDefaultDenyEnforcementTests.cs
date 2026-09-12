using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Test.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Enforcement-path proof for <c>:root { policy-default: deny }</c>: the mutation
/// transformer must both claim a table it has never seen policy metadata on and
/// fail it closed. The model is built by <see cref="PolicyDefaultDenyModel"/>
/// through the production loader, so a stamp that never reaches the table shows
/// up here as a transformer that simply does not apply.
/// </summary>
public sealed class PolicyDefaultDenyEnforcementTests
{
    [Fact]
    public async Task Insert_OnUndeclaredTableUnderDeny_IsAccessDeniedForNonAdmin()
    {
        await using var loaded = await PolicyDefaultDenyModel.LoadAsync(
            ":root { policy-default: deny }");

        var table = loaded.Table();
        var transformer = new PolicyMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = loaded.Model,
            UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "user-1",
                ["roles"] = Array.Empty<string>(),
            },
        };

        transformer.AppliesTo(table, MutationType.Insert, context)
            .Should().BeTrue("the deny default gives every table a policy");

        var result = await transformer.TransformAsync(
            table, MutationType.Insert, new Dictionary<string, object?> { ["body"] = "x" }, context);

        result.Errors.Should().NotBeNullOrEmpty();
        result.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }
}

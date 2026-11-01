using BifrostQL.Core.Operations;
using FluentAssertions;
using Xunit;

namespace BifrostQL.PublicApi.Test;

/// <summary>
/// Stands in for a third-party operation author: this assembly has no
/// <c>InternalsVisibleTo</c> grant from BifrostQL.Core, so a green build proves an operation —
/// its declaration, arguments, secured reads and writes, and its return value — can be
/// written against public types only.
/// </summary>
public sealed class ServerOperationConsumerTests
{
    private sealed class RenameClaim : IBifrostOperation
    {
        public string Name => "renameClaim";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(id: Int!, title: String!): RenameResult!";
        public string TypeDefinitions => "type RenameResult { changed: Int! title: String }";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            var id = context.GetArgument<int>("id");
            var before = await context.QueryAsync("claims", new Dictionary<string, object?> { ["id"] = id }, limit: 1);
            var changed = await context.UpdateAsync("claims", new Dictionary<string, object?>
            {
                ["id"] = id, ["title"] = context.GetArgument<string>("title"),
            });
            return new Dictionary<string, object?>
            {
                ["changed"] = changed,
                ["title"] = before.Count == 0 ? null : before[0]["title"],
                ["by"] = context.Identity.Id,
            };
        }
    }

    [Fact]
    public void AnOperationCompilesAgainstPublicTypesOnly()
    {
        IBifrostOperation operation = new RenameClaim();
        operation.Kind.Should().Be(BifrostOperationKind.Mutation);
        operation.TypeDefinitions.Should().Contain("RenameResult");
    }
}

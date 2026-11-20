using System.Collections.Concurrent;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A transformer can tell an operation's write from a client's: <c>MutationTransformContext.Operation</c>
/// names the operation on every write its context makes and is null on a generated write, including
/// a sibling mutation field of the same request that runs right after it.
/// </summary>
public sealed class OperationWriteOriginTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE notes (id SERIAL PRIMARY KEY, title TEXT NOT NULL);
        """;

    private readonly ConcurrentQueue<string> _seen = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_write_origin", Ddl, new[] { ":root { operations: addNote[writer] }" },
            bifrost: options => options
                .AddOperation<AddNote>()
                .AddMutationTransformers(new IMutationTransformer[] { new OriginRecorder(_seen) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Transformer_SeesTheOperation_OnItsWrite_AndNull_OnASiblingGeneratedWrite()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync(
            """mutation { addNote(title: "from-op") notes(insert: { title: "from-client" }) }""", "u-1", "writer");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _seen.Should().Equal("from-op:addNote", "from-client:<none>");
    }

    private sealed class AddNote : IBifrostOperation
    {
        public string Name => "addNote";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(title: String!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => await context.InsertAsync("notes", new Dictionary<string, object?> { ["title"] = context.GetArgument<string>("title") });
    }

    private sealed class OriginRecorder(ConcurrentQueue<string> seen) : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => table.DbName == "notes";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            seen.Enqueue($"{data["title"]}:{context.Operation?.Name ?? "<none>"}");
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }
}

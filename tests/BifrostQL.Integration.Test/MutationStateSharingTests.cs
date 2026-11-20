using System.Collections.Concurrent;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Integration.Test;

/// <summary>
/// One bag per write: <c>MutationTransformContext.MutationState</c> is the instance both hook
/// phases of that write receive, so a transformer that locked and loaded the row hands it to the
/// after-write hook instead of the hook reading it again. The next write of the same request
/// (a sibling field, or the next action of a batch) starts with an empty bag, and an operation
/// can start its write's bag with entries of its own.
/// </summary>
public sealed class MutationStateSharingTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        INSERT INTO items (name) VALUES ('one'), ('two');
        """;

    private const string LoadedRowKey = "test:loaded-row";
    private const string SeedKey = "test:seed";

    private readonly ConcurrentQueue<string> _log = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_mutation_state", Ddl, new[] { ":root { operations: renameWithSeed[writer] }" },
            services => services.AddSingleton<IInTransactionMutationHook>(new StateReadingHook(_log)),
            options => options
                .AddOperation<RenameWithSeed>()
                .AddMutationTransformers(new IMutationTransformer[] { new RowLoadingTransformer(_log) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task TransformerState_ReachesTheInTransactionHook_OfTheSameWrite()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync(
            """mutation { a: items(update: { id: 1, name: "uno" }) b: items(update: { id: 2, name: "dos" }) }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _log.Should().Equal(
            "transform:1:empty", "hook:1:same-row",
            "transform:2:empty", "hook:2:same-row");
    }

    [SkippableFact]
    public async Task TransformerState_IsPerAction_InABatch()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync(
            """mutation { items_batch(actions: [{ update: { id: 1, name: "uno" } }, { update: { id: 2, name: "dos" } }]) }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _log.Should().Equal(
            "transform:1:empty", "hook:1:same-row",
            "transform:2:empty", "hook:2:same-row");
    }

    [SkippableFact]
    public async Task OperationState_ReachesTheHooks_OfItsWrite()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { renameWithSeed(id: 1, name: "uno") }""", "u-1", "writer");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _log.Should().Equal("transform:1:seeded", "hook:1:same-row", "hook:1:seed=from-operation");
    }

    private static string KeyOf(IDictionary<string, object?> data)
        => Convert.ToString(data["id"], System.Globalization.CultureInfo.InvariantCulture)!;

    /// <summary>Locks and loads the row being updated and leaves it in the write's bag.</summary>
    private sealed class RowLoadingTransformer(ConcurrentQueue<string> log) : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Update && table.DbName == "items" && context.CurrentRow is null;

        public async ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            var start = context.MutationState.ContainsKey(SeedKey) ? "seeded"
                : context.MutationState.Count == 0 ? "empty" : "dirty";
            log.Enqueue($"transform:{KeyOf(data)}:{start}");
            context.MutationState[LoadedRowKey] = await context.LoadRowForUpdateAsync(
                table, new[] { "id", "name" }, new Dictionary<string, object?> { ["id"] = data["id"] });
            return new MutationTransformResult { MutationType = mutationType, Data = data };
        }
    }

    private sealed class StateReadingHook(ConcurrentQueue<string> log) : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName == "items";

        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            var key = KeyOf(context.Data);
            var row = context.MutationState.TryGetValue(LoadedRowKey, out var loaded)
                ? loaded as IReadOnlyDictionary<string, object?>
                : null;
            log.Enqueue(row is not null && KeyOf(row.ToDictionary(kv => kv.Key, kv => kv.Value)) == key
                ? $"hook:{key}:same-row"
                : $"hook:{key}:no-row");
            if (context.MutationState.TryGetValue(SeedKey, out var seed))
                log.Enqueue($"hook:{key}:seed={seed}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RenameWithSeed : IBifrostOperation
    {
        public string Name => "renameWithSeed";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(id: Int!, name: String!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => await context.UpdateAsync(
                "items",
                new Dictionary<string, object?> { ["id"] = context.GetArgument<int>("id"), ["name"] = context.GetArgument<string>("name") },
                new Dictionary<string, object?> { [SeedKey] = "from-operation" });
    }
}

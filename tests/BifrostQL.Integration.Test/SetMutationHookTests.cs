using System.Collections.Concurrent;
using System.Globalization;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Integration.Test;

/// <summary>
/// An <see cref="IInTransactionSetMutationHook"/> is called once per write with every written
/// row's data, key and state, on the write's transaction; a single-row write hands it one row; a
/// per-row <see cref="IInTransactionMutationHook"/> keeps one call per row. Statement-level
/// triggers count the INSERT statements the database ran on <c>items</c> and <c>activity</c>.
/// Each fact uses its own caller id, which the hooks record calls under.
/// </summary>
public sealed class SetMutationHookTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, n INT NOT NULL);
        CREATE TABLE activity (id SERIAL PRIMARY KEY, item_id INT NOT NULL, owner_id INT NOT NULL, state TEXT NOT NULL);
        CREATE TABLE insert_statements (owner_id INT NOT NULL);
        CREATE FUNCTION count_insert() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN INSERT INTO insert_statements SELECT DISTINCT owner_id FROM written; RETURN NULL; END $$;
        CREATE TRIGGER items_insert_statements AFTER INSERT ON items
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        CREATE TRIGGER activity_insert_statements AFTER INSERT ON activity
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        """;

    private const string HookThrowsFor = "66";

    private readonly ConcurrentQueue<(string User, int Rows)> _setCalls = new();
    private readonly ConcurrentQueue<string> _rowCalls = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_set_hook", Ddl,
            new[] { ":root { operations: insertMany[clerk] }", "public.items.owner_id { populate: host }" },
            services: services => services
                .AddSingleton<IInTransactionMutationHook>(new ActivityWriter(_setCalls))
                .AddSingleton<IInTransactionMutationHook>(new RowCounter(_rowCalls)),
            bifrost: options => options
                .AddOperation<InsertMany>()
                .AddMutationTransformers(new IMutationTransformer[] { new StampOwner() }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task SetHook_IsCalledOnce_WithEveryRowAndKey_OfAnInsertMany()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertMany }""", "11", "clerk");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _setCalls.Where(c => c.User == "11").Should().Equal(new[] { ("11", 25) }, "one call, every row");
        (await _host.CountAsync(
            "SELECT count(*) FROM activity a JOIN items i ON i.id = a.item_id WHERE a.owner_id = 11 AND a.state = 'n=' || i.n"))
            .Should().Be(25, "each activity row names its item's key and carries that row's own state");
        (await _host.CountAsync("SELECT count(*) FROM insert_statements WHERE owner_id = 11"))
            .Should().Be(2, "25 items and their 25 activity rows are 2 INSERT statements");
    }

    [SkippableFact]
    public async Task SetHook_Throw_LeavesNoneOfTheRows()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertMany }""", HookThrowsFor, "clerk");

        PostgresGraphQlHost.FirstError(reply);
        _setCalls.Should().Contain(c => c.User == HookThrowsFor, "the hook ran and wrote before it threw");
        (await _host.CountAsync($"SELECT count(*) FROM items WHERE owner_id = {HookThrowsFor}")).Should().Be(0);
        (await _host.CountAsync($"SELECT count(*) FROM activity WHERE owner_id = {HookThrowsFor}")).Should().Be(0);
    }

    [SkippableFact]
    public async Task SetHook_GetsOneRow_OnASingleRowWrite()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { items(insert: { n: 5 }) }""", "12");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var key = reply.RootElement.GetProperty("data").GetProperty("items").GetInt32();
        _setCalls.Where(c => c.User == "12").Should().Equal(new[] { ("12", 1) });
        (await _host.CountAsync($"SELECT count(*) FROM activity WHERE owner_id = 12 AND item_id = {key} AND state = 'n=5'"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task PerRowHook_StillRunsPerRow_OnAnInsertMany()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertMany }""", "13", "clerk");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _rowCalls.Count(u => u == "13").Should().Be(25);
    }

    private sealed class InsertMany : IBifrostOperation
    {
        public string Name => "insertMany";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => (await context.InsertManyAsync("items", Enumerable.Range(1, 25)
                .Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["n"] = n }))).Count;
    }

    /// <summary>Stamps the caller as owner, and leaves <c>n=&lt;n&gt;</c> in the row's state bag.</summary>
    private sealed class StampOwner : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Insert && table.DbName == "items";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            data["owner_id"] = int.Parse(PolicyIdentity.FromUserContext(context.UserContext).Id, CultureInfo.InvariantCulture);
            context.MutationState["activity.state"] = $"n={data["n"]}";
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }

    /// <summary>Writes one activity row per written item, all in one INSERT, on the write's transaction.</summary>
    private sealed class ActivityWriter(ConcurrentQueue<(string, int)> calls) : IInTransactionSetMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName == "items";

        public async ValueTask AfterSetWriteInTransactionAsync(MutationSetContext context)
        {
            var user = PolicyIdentity.FromUserContext(context.UserContext).Id;
            calls.Enqueue((user, context.Rows.Count));
            await using var cmd = context.Connection.CreateCommand();
            cmd.Transaction = context.Transaction;
            var values = new List<string>();
            for (var i = 0; i < context.Rows.Count; i++)
            {
                values.Add($"(@k{i}, @o{i}, @s{i})");
                AddParameter(cmd, $"k{i}", Convert.ToInt32(context.Rows[i].Key, CultureInfo.InvariantCulture));
                AddParameter(cmd, $"o{i}", context.Rows[i].Data["owner_id"]);
                AddParameter(cmd, $"s{i}", context.Rows[i].MutationState["activity.state"]);
            }
            cmd.CommandText = $"INSERT INTO activity (item_id, owner_id, state) VALUES {string.Join(",", values)}";
            await cmd.ExecuteNonQueryAsync(context.CancellationToken);
            if (user == HookThrowsFor)
                throw new InvalidOperationException("the activity writer failed after writing");
        }

        private static void AddParameter(System.Data.Common.DbCommand cmd, string name, object? value)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
    }

    /// <summary>A per-row hook: records the caller once per written item.</summary>
    private sealed class RowCounter(ConcurrentQueue<string> calls) : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName == "items";

        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            calls.Enqueue(PolicyIdentity.FromUserContext(context.UserContext).Id);
            return ValueTask.CompletedTask;
        }
    }
}

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
/// row's verb, data, key and state, in row order, on the write's transaction: one row for a
/// single-row write, every row of an operation's <c>DeltaAsync</c> or a <c>&lt;table&gt;_batch</c>.
/// A table whose applying hooks are all set-aware keeps its rows in one statement; a hook that is
/// not set-aware keeps one call per row, each before the next row's statement.
/// <c>entries</c> carries only the set-aware activity writer; <c>items</c> also a per-row hook.
/// Statement-level triggers record each INSERT statement under the caller that wrote it.
/// </summary>
public sealed class SetAwareHookTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, n INT NOT NULL);
        CREATE TABLE notebooks (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, n INT NOT NULL);
        CREATE TABLE entries (id SERIAL PRIMARY KEY, notebook_id INT REFERENCES notebooks(id), owner_id INT NOT NULL, n INT NOT NULL);
        CREATE TABLE activity (id SERIAL PRIMARY KEY, row_id INT NOT NULL, owner_id INT NOT NULL, state TEXT NOT NULL);
        CREATE TABLE insert_statements (owner_id INT NOT NULL, table_name TEXT NOT NULL);
        CREATE FUNCTION count_insert() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN INSERT INTO insert_statements SELECT DISTINCT owner_id, TG_TABLE_NAME FROM written; RETURN NULL; END $$;
        CREATE TRIGGER items_insert_statements AFTER INSERT ON items
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        CREATE TRIGGER notebooks_insert_statements AFTER INSERT ON notebooks
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        CREATE TRIGGER entries_insert_statements AFTER INSERT ON entries
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        CREATE TRIGGER activity_insert_statements AFTER INSERT ON activity
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        """;

    private const string HookThrowsFor = "66";

    private readonly ConcurrentQueue<SetCall> _setCalls = new();
    private readonly ConcurrentQueue<(string User, long RowsWrittenSoFar)> _rowCalls = new();
    private PostgresGraphQlHost? _host;

    private sealed record SetCall(string User, string Table, IReadOnlyList<MutationType> Types, IReadOnlyList<int> Keys, IReadOnlyList<int> Ns);

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_set_aware_hook", Ddl,
            new[]
            {
                ":root { operations: deltaInto[clerk] }",
                "public.items.owner_id { populate: host }",
                "public.entries.owner_id { populate: host }",
                "public.notebooks.owner_id { populate: host }",
            },
            services: services => services
                .AddSingleton<IInTransactionMutationHook>(new ActivityWriter(_setCalls))
                .AddSingleton<IInTransactionMutationHook>(new RowCounter(_rowCalls)),
            bifrost: options => options
                .AddOperation<DeltaInto>()
                .AddMutationTransformers(new IMutationTransformer[] { new StampOwner() }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task SetAwareHook_IsCalledOnce_WithEveryRowKeyAndState_OfAnOperationsDelta()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { deltaInto(table: "entries", count: 25) }""", "11", "clerk");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var call = _setCalls.Where(c => c.User == "11").Should().ContainSingle("one call, every row").Subject;
        call.Ns.Should().Equal(Enumerable.Range(1, 25), "rows arrive in row order");
        call.Types.Should().OnlyContain(t => t == MutationType.Insert);
        call.Keys.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        (await _host.CountAsync(
            "SELECT count(*) FROM activity a JOIN entries e ON e.id = a.row_id WHERE a.owner_id = 11 AND a.state = 'n=' || e.n"))
            .Should().Be(25, "each activity row names its row's key and carries that row's own state");
        (await _host.CountAsync("SELECT count(*) FROM insert_statements WHERE owner_id = 11"))
            .Should().Be(2, "25 entries and their 25 activity rows are 2 INSERT statements");

        var thrown = await _host.PostAsync("""mutation { deltaInto(table: "entries", count: 25) }""", HookThrowsFor, "clerk");

        PostgresGraphQlHost.FirstError(thrown);
        _setCalls.Should().Contain(c => c.User == HookThrowsFor, "the hook ran and wrote before it threw");
        (await _host.CountAsync($"SELECT count(*) FROM entries WHERE owner_id = {HookThrowsFor}")).Should().Be(0);
        (await _host.CountAsync($"SELECT count(*) FROM activity WHERE owner_id = {HookThrowsFor}")).Should().Be(0);
    }

    [SkippableFact]
    public async Task SetAwareHook_GetsOneRow_OnASingleRowWrite()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { entries(insert: { n: 5 }) }""", "12");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var key = reply.RootElement.GetProperty("data").GetProperty("entries").GetInt32();
        _setCalls.Where(c => c.User == "12").Should().ContainSingle()
            .Which.Keys.Should().Equal(key);
        (await _host.CountAsync($"SELECT count(*) FROM activity WHERE owner_id = 12 AND row_id = {key} AND state = 'n=5'"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task SetAwareHook_GetsTheRowsKey_OnASingleRowUpdate()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        PostgresGraphQlHost.AssertNoErrors(await _host!.PostAsync("""mutation { entries(insert: { n: 1 }) }""", "15"));
        var second = await _host.PostAsync("""mutation { entries(insert: { n: 2 }) }""", "15");
        var key = second.RootElement.GetProperty("data").GetProperty("entries").GetInt32();

        var reply = await _host.PostAsync($$"""mutation { entries(update: { id: {{key}}, n: 3 }) }""", "15");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var update = _setCalls.Where(c => c.User == "15").Last();
        update.Types.Should().Equal(MutationType.Update);
        update.Keys.Should().Equal(new[] { key }, "an update's key is the row's key, not its affected-row count");
    }

    [SkippableFact]
    public async Task HookedTable_KeepsTheBulkPath_WhenItsHooksAreSetAware()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var actions = string.Join(", ", Enumerable.Range(1, 50).Select(n => $"{{ insert: {{ n: {n} }} }}"));
        var reply = await _host!.PostAsync($$"""mutation { entries_batch(actions: [{{actions}}]) }""", "14");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var call = _setCalls.Where(c => c.User == "14").Should().ContainSingle("one call for the whole batch").Subject;
        call.Ns.Should().Equal(Enumerable.Range(1, 50));
        var rows = await _host.RowsAsync("SELECT id, n FROM entries WHERE owner_id = 14 ORDER BY n");
        call.Keys.Should().Equal(rows.Select(r => int.Parse(r["id"]!, CultureInfo.InvariantCulture)), "each key is its own row's");
        (await _host.CountAsync("SELECT count(*) FROM insert_statements WHERE owner_id = 14 AND table_name = 'entries'"))
            .Should().Be(1, "50 inserts on a table whose only hook is set-aware are one INSERT");
    }

    [SkippableFact]
    public async Task SetAwareHook_IsCalledOncePerLevel_OfASave()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var entries = string.Join(", ", Enumerable.Range(1, 10).Select(n => $"{{ n: {n} }}"));
        var reply = await _host!.PostAsync($$"""mutation { notebooks(save: { n: 0, entries: [{{entries}}] }) }""", "16");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var root = reply.RootElement.GetProperty("data").GetProperty("notebooks").GetInt32();
        var calls = _setCalls.Where(c => c.User == "16").ToList();
        calls.Select(c => c.Table).Should().Equal(new[] { "notebooks", "entries" }, "one call per level");
        calls[0].Keys.Should().Equal(root);
        calls[1].Ns.Should().Equal(Enumerable.Range(1, 10));
        var rows = await _host.RowsAsync($"SELECT id FROM entries WHERE owner_id = 16 AND notebook_id = {root} ORDER BY n");
        calls[1].Keys.Should().Equal(rows.Select(r => int.Parse(r["id"]!, CultureInfo.InvariantCulture)), "each key is its own row's");
        (await _host.CountAsync("SELECT count(*) FROM insert_statements WHERE owner_id = 16"))
            .Should().Be(4, "each level and its activity rows are one INSERT apiece");
    }

    [SkippableFact]
    public async Task PerRowHook_KeepsItsPerRowCalls()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { deltaInto(table: "items", count: 25) }""", "13", "clerk");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _rowCalls.Where(c => c.User == "13").Select(c => c.RowsWrittenSoFar)
            .Should().Equal(Enumerable.Range(1, 25).Select(n => (long)n), "each call comes before the next row's statement");
        _setCalls.Where(c => c.User == "13").Should().ContainSingle().Which.Ns.Should().Equal(Enumerable.Range(1, 25));
    }

    private sealed class DeltaInto : IBifrostOperation
    {
        public string Name => "deltaInto";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(table: String!, count: Int!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => await context.DeltaAsync(context.GetArgument<string>("table")!, new BifrostDelta
            {
                Inserted = Enumerable.Range(1, context.GetArgument<int>("count"))
                    .Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["n"] = n }).ToList(),
            });
    }

    /// <summary>Stamps the caller as owner, and leaves <c>n=&lt;n&gt;</c> in the row's state bag.</summary>
    private sealed class StampOwner : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Insert && table.DbName is "items" or "entries" or "notebooks";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            data["owner_id"] = int.Parse(PolicyIdentity.FromUserContext(context.UserContext).Id, CultureInfo.InvariantCulture);
            context.MutationState["activity.state"] = $"n={data["n"]}";
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }

    /// <summary>Writes one activity row per inserted row, all in one INSERT, on the write's transaction.</summary>
    private sealed class ActivityWriter(ConcurrentQueue<SetCall> calls) : IInTransactionSetMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName is "items" or "entries" or "notebooks";

        public async ValueTask AfterSetWriteInTransactionAsync(MutationSetContext context)
        {
            var user = PolicyIdentity.FromUserContext(context.UserContext).Id;
            calls.Enqueue(new SetCall(
                user, context.Table.DbName,
                context.Rows.Select(r => r.MutationType).ToList(),
                context.Rows.Select(r => Convert.ToInt32(r.Key, CultureInfo.InvariantCulture)).ToList(),
                context.Rows.Select(r => Convert.ToInt32(r.Data["n"], CultureInfo.InvariantCulture)).ToList()));
            var inserted = context.Rows.Where(r => r.MutationType == MutationType.Insert).ToList();
            if (inserted.Count == 0)
                return;
            await using var cmd = context.Connection.CreateCommand();
            cmd.Transaction = context.Transaction;
            var values = new List<string>();
            for (var i = 0; i < inserted.Count; i++)
            {
                values.Add($"(@k{i}, @o{i}, @s{i})");
                AddParameter(cmd, $"k{i}", Convert.ToInt32(inserted[i].Key, CultureInfo.InvariantCulture));
                AddParameter(cmd, $"o{i}", inserted[i].Data["owner_id"]);
                AddParameter(cmd, $"s{i}", inserted[i].MutationState["activity.state"]);
            }
            cmd.CommandText = $"INSERT INTO activity (row_id, owner_id, state) VALUES {string.Join(",", values)}";
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

    /// <summary>A per-row hook on <c>items</c>: records how many of the caller's items exist when it is called.</summary>
    private sealed class RowCounter(ConcurrentQueue<(string, long)> calls) : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName == "items";

        public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            var user = PolicyIdentity.FromUserContext(context.UserContext).Id;
            await using var cmd = context.Connection!.CreateCommand();
            cmd.Transaction = context.Transaction;
            cmd.CommandText = $"SELECT count(*) FROM items WHERE owner_id = {int.Parse(user, CultureInfo.InvariantCulture)}";
            calls.Enqueue((user, Convert.ToInt64(await cmd.ExecuteScalarAsync(context.CancellationToken), CultureInfo.InvariantCulture)));
        }
    }
}

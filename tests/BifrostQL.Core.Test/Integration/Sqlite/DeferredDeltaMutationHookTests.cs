using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Deferred;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>End-to-end SQLite coverage for durable reverse deltas.</summary>
public sealed class DeferredDeltaMutationHookTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_deferred_delta_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var table in new[] { "__history", "soft_widgets", "widgets", "change_set_deltas", "change_sets" })
            await Exec($"DROP TABLE IF EXISTS {table}");
        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL CHECK (name <> 'boom'), version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE soft_widgets (id INTEGER PRIMARY KEY, name TEXT NULL, deleted_at TEXT NULL, version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE __history (id INTEGER PRIMARY KEY, entity TEXT NOT NULL, entity_id TEXT NOT NULL, op TEXT NOT NULL, actor TEXT NULL, changed_at TEXT NOT NULL, before TEXT NULL, after TEXT NULL, changed_columns TEXT NULL)");
        await Exec("CREATE TABLE change_sets (id INTEGER PRIMARY KEY, state TEXT NOT NULL, undo_window_expires_at TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, tables TEXT NOT NULL, created_at TEXT NOT NULL, applied_at TEXT NULL, reversed_at TEXT NULL)");
        await Exec("CREATE TABLE change_set_deltas (id INTEGER PRIMARY KEY, change_set_id INTEGER NOT NULL, \"table\" TEXT NOT NULL, pk TEXT NOT NULL, op TEXT NOT NULL, inverse_op TEXT NOT NULL, before_image TEXT NULL, after_image TEXT NULL, created_at TEXT NOT NULL)");
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'original')");
        await Exec("INSERT INTO soft_widgets(id, name) VALUES (1, 'soft-original')");
        _model = await LoadModelAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task SingleRowWrites_RecordReversibleInsertUpdateDeleteAndSoftDeleteDeltas()
    {
        (await ExecuteMutationAsync("mutation { widgets(insert: { name: \"new\" }) }")).Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { widgets(update: { id: 1, name: \"edited\" }) }")).Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { widgets(delete: { id: 1 }) }")).Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { soft_widgets(delete: { id: 1 }) }")).Errors.Should().BeNullOrEmpty();

        var deltas = await DeltasAsync();
        deltas.Should().HaveCount(4);
        deltas.Select(d => (d.Op, d.InverseOp)).Should().Equal(("insert", "delete"), ("update", "restore"), ("delete", "restore"), ("update", "restore"));
        deltas[0].BeforeImage.Should().BeNull();
        Json(deltas[1].BeforeImage!)["name"].GetString().Should().Be("original");
        Json(deltas[2].BeforeImage!)["name"].GetString().Should().Be("edited");
        Json(deltas[3].BeforeImage!)["deleted_at"].ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("soft_widgets", "id = 1 AND deleted_at IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task BatchUpsertThatInserts_RecordsDeleteInverseForInsertedKey()
    {
        var result = await ExecuteMutationAsync("mutation { widgets_batch(actions: [ { upsert: { id: 77, name: \"fresh\" } } ]) }");

        result.Errors.Should().BeNullOrEmpty();
        var delta = (await DeltasAsync()).Should().ContainSingle().Subject;
        delta.Op.Should().Be("insert");
        delta.InverseOp.Should().Be("delete");
        Json(delta.Pk)["id"].GetInt64().Should().Be(77);
        delta.BeforeImage.Should().BeNull();
    }

    [Fact]
    public async Task Batch_RecordsEveryDeltaInOneChangeSetTransaction()
    {
        var result = await ExecuteMutationAsync("mutation { widgets_batch(actions: [ { insert: { name: \"a\" } }, { update: { id: 1, name: \"batch-edited\" } } ]) }");

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("change_sets", "1=1")).Should().Be(1);
        var deltas = await DeltasAsync();
        deltas.Should().HaveCount(2);
        deltas.Select(d => d.ChangeSetId).Distinct().Should().ContainSingle("a batch shares its mutation transaction and active change set");
        (await CountAsync("widgets", "name = 'batch-edited'")).Should().Be(1);
    }

    [Fact]
    public async Task TreeSync_RecordsDeltasWithConnectionScopedTransactionState()
    {
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NULL, version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE posts (id INTEGER PRIMARY KEY, blog_id INTEGER NOT NULL REFERENCES blogs(id), title TEXT NULL, version INTEGER NULL DEFAULT 1)");
        var model = await LoadModelAsync("main.blogs { deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled }", "main.posts { deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled }");

        var result = await ExecuteMutationAsync("mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" } ] }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        var deltas = await DeltasAsync();
        deltas.Should().HaveCount(2);
        deltas.Select(d => d.Table).Should().BeEquivalentTo("main.blogs", "main.posts");
        deltas.Select(d => d.ChangeSetId).Distinct().Should().ContainSingle("TreeSync shares its connection-scoped active state when its transaction is null");
    }

    [Fact]
    public async Task HookFailure_RollsBackDataChangeSetAndDeltaAtomically()
    {
        var result = await ExecuteMutationAsync("mutation { widgets(insert: { name: \"will-roll-back\" }) }", addFailingHook: true);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("widgets", "name = 'will-roll-back'")).Should().Be(0);
        (await CountAsync("change_sets", "1=1")).Should().Be(0);
        (await CountAsync("change_set_deltas", "1=1")).Should().Be(0);
    }

    [Fact]
    public async Task MissingBeforeImage_FailsClosedAndRollsBackWrite()
    {
        var result = await ExecuteMutationAsync("mutation { widgets(update: { id: 1, name: \"must-not-commit\" }) }", registerHistory: false);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("widgets", "id = 1 AND name = 'original'")).Should().Be(1);
        (await CountAsync("change_set_deltas", "1=1")).Should().Be(0);
    }

    [Theory]
    [InlineData("change_sets", "state: \"held\", undo_window_expires_at: \"2026-07-21T00:00:00Z\", tables: \"[]\", created_at: \"2026-07-20T00:00:00Z\"")]
    [InlineData("change_set_deltas", "change_set_id: 1, table: \"main.widgets\", pk: \"{}\", op: \"insert\", inverse_op: \"delete\", created_at: \"2026-07-20T00:00:00Z\"")]
    public async Task DeferredStore_PublicMutation_IsRejected(string table, string fields)
    {
        var result = await ExecuteMutationAsync($"mutation {{ {table}(insert: {{ {fields} }}) }}");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Should().Contain(e => e.Message.Contains("internal deferred-effects table") && e.Message.Contains("not writable"));
    }

    private static async Task<IDbModel> LoadModelAsync(params string[] extra) => await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[]
    {
        "main.widgets { deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled }",
        "main.soft_widgets { deferrable: enabled; undo-window: 1h; soft-delete: deleted_at; concurrency-token: version; history: enabled }",
        ":root { history-table: main.__history }",
    }.Concat(extra).ToArray())).LoadAsync();

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation, IDbModel? model = null, bool registerHistory = true, bool addFailingHook = false)
    {
        model ??= _model;
        var schema = DbSchema.FromModel(model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() } });
        if (registerHistory)
        {
            services.AddSingleton<HistoryMutationHook>();
            services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
            services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        }
        services.AddSingleton<IInTransactionMutationHook, DeferredDeltaMutationHook>();
        if (addFailingHook)
            services.AddSingleton<IInTransactionMutationHook, FailingHook>();
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString), ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }

    private sealed class FailingHook : IInTransactionMutationHook
    {
        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context) => throw new InvalidOperationException("forced deferred rollback");
    }

    private sealed record Delta(long ChangeSetId, string Table, string Pk, string Op, string InverseOp, string? BeforeImage);
    private async Task<List<Delta>> DeltasAsync()
    {
        var result = new List<Delta>();
        await using var command = new SqliteCommand("SELECT change_set_id, \"table\", pk, op, inverse_op, before_image FROM change_set_deltas ORDER BY id", _keepAlive);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    private static Dictionary<string, JsonElement> Json(string value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value)!;
    private async Task<long> CountAsync(string table, string where)
    {
        await using var command = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private async Task Exec(string sql)
    {
        await using var command = new SqliteCommand(sql, _keepAlive);
        await command.ExecuteNonQueryAsync();
    }
}

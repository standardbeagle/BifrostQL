using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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

/// <summary>
/// End-to-end proof of the change-history writer (slice 2). An orders table records
/// history; the built-in <see cref="HistoryMutationHook"/> captures the row before the
/// write and writes the before/after diff into the history table in the SAME transaction
/// as the change. The guarantees pinned here: the trail rolls back with a rejected write,
/// a no-op write records nothing, and an update that moved no tracked column records
/// nothing.
/// </summary>
public sealed class HistoryMutationHookTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_history_hook_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS __history");
        await Exec(
            """
            -- Nullable columns keep every mutation's input fields optional, so a test can
            -- update one column at a time (a NOT NULL column is a required GraphQL input).
            CREATE TABLE orders (
                id     INTEGER PRIMARY KEY,
                status TEXT NULL CHECK (status <> 'boom'),
                total  REAL NULL,
                note   TEXT NULL
            )
            """);
        // The history table, matching the documented column contract.
        await Exec(
            """
            CREATE TABLE __history (
                id              INTEGER PRIMARY KEY,
                entity          TEXT NOT NULL,
                entity_id       TEXT NOT NULL,
                op              TEXT NOT NULL,
                actor           TEXT NULL,
                changed_at      TEXT NOT NULL,
                before          TEXT NULL,
                after           TEXT NULL,
                changed_columns TEXT NULL
            )
            """);
        await Exec("INSERT INTO orders(id, status, total, note) VALUES (1, 'packing', 240.0, 'first')");

        _model = await LoadModelAsync(
            "main.orders { history: enabled; history-columns: status,total }",
            ":root { history-table: main.__history }");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static async Task<IDbModel> LoadModelAsync(params string[] metadata) =>
        await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(metadata)).LoadAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed record HistoryRow(
        string Entity, string EntityId, string Op, string? Actor, string? Before, string? After, string ChangedColumns);

    private async Task<List<HistoryRow>> HistoryRowsAsync()
    {
        var rows = new List<HistoryRow>();
        await using var cmd = new SqliteCommand(
            "SELECT entity, entity_id, op, actor, before, after, changed_columns FROM __history ORDER BY id",
            _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new HistoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }
        return rows;
    }

    private static Dictionary<string, JsonElement> Json(string? value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value!)!;

    private static string[] JsonArray(string value) =>
        JsonSerializer.Deserialize<string[]>(value)!;

    [Fact]
    public async Task Update_RecordsBeforeAndAfterImages_AndTheChangedColumn()
    {
        // Act
        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }");

        // Assert
        result.Errors.Should().BeNullOrEmpty();

        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Entity.Should().Be("main.orders");
        rows[0].Op.Should().Be("update");
        Json(rows[0].EntityId)["id"].GetInt64().Should().Be(1);

        Json(rows[0].Before)["status"].GetString().Should().Be("packing");
        Json(rows[0].After)["status"].GetString().Should().Be("shipped");
        // total is tracked and appears in both images, but it did not move.
        Json(rows[0].Before)["total"].GetDouble().Should().Be(240.0);
        JsonArray(rows[0].ChangedColumns).Should().Equal("status");
    }

    [Fact]
    public async Task Insert_RecordsAfterImageOnly_WithGeneratedKey()
    {
        var result = await ExecuteMutationAsync(
            "mutation { orders(insert: { status: \"new\", total: 10.5 }) }");

        result.Errors.Should().BeNullOrEmpty();

        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Op.Should().Be("insert");
        rows[0].Before.Should().BeNull("an insert has no before-image");
        // The client supplied no id; the trail must still name the row, from the
        // database-generated identity.
        Json(rows[0].EntityId)["id"].GetInt64().Should().BeGreaterThan(1);
        Json(rows[0].After)["status"].GetString().Should().Be("new");
        JsonArray(rows[0].ChangedColumns).Should().BeEquivalentTo(new[] { "status", "total" });
    }

    [Fact]
    public async Task Delete_RecordsBeforeImageOnly()
    {
        var result = await ExecuteMutationAsync("mutation { orders(delete: { id: 1 }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "id = 1")).Should().Be(0);

        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Op.Should().Be("delete");
        Json(rows[0].Before)["status"].GetString().Should().Be("packing");
        rows[0].After.Should().BeNull("a deleted row has no after-image");
        JsonArray(rows[0].ChangedColumns).Should().BeEquivalentTo(new[] { "status", "total" });
    }

    [Fact]
    public async Task Update_TouchingNoTrackedColumn_RecordsNothing()
    {
        // note is not in history-columns, so changing it moves nothing this table records.
        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, note: \"edited\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "note = 'edited'")).Should().Be(1, "the data change still happened");
        (await HistoryRowsAsync()).Should().BeEmpty("no tracked column moved");
    }

    [Fact]
    public async Task Update_MatchingNoRow_RecordsNothing()
    {
        // A zero-row update changed nothing; recording it would fabricate a change.
        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 999, status: \"shipped\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await HistoryRowsAsync()).Should().BeEmpty("no row was affected");
    }

    [Fact]
    public async Task FailedWrite_RollsBackDataAndHistory_Atomically()
    {
        // The hook writes its history row inside the transaction, then the CHECK constraint
        // rejects status='boom'. Both must roll back — no trail entry for a change that
        // never committed.
        var result = await ExecuteMutationAsync(
            "mutation { orders(insert: { status: \"boom\", total: 1 }) }");

        result.Errors.Should().NotBeNullOrEmpty("the CHECK violation aborts the transaction");
        (await CountAsync("orders", "status = 'boom'")).Should().Be(0, "the data write rolled back");
        (await HistoryRowsAsync()).Should().BeEmpty(
            "the history row must roll back with the data change it describes");
    }

    [Fact]
    public async Task Actor_IsRecordedFromTheAuditUserContext()
    {
        // The trail's actor resolves exactly like the audit columns' updated-by: the
        // user-context claim named by the model-level user-audit-key.
        var model = await LoadModelAsync(
            "main.orders { history: enabled; history-columns: status,total }",
            ":root { history-table: main.__history; user-audit-key: sub }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }",
            model,
            userContext: new Dictionary<string, object?> { ["sub"] = "u_44" });

        result.Errors.Should().BeNullOrEmpty();
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Actor.Should().Be("u_44");
    }

    [Fact]
    public async Task WorkflowTriggeredUpdate_IsRecorded_NotRolledBack()
    {
        // A workflow-triggered mutation carries the trigger-suppression flag. That flag
        // stops a workflow from re-firing itself in the POST-commit observer phase; it must
        // not skip the pre-write phase, or the write would reach the recorder with no
        // before-image and be rolled back. A workflow's change is a real change: record it.
        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }",
            userContext: new Dictionary<string, object?>
            {
                [BifrostQL.Core.Workflows.WorkflowTriggerHost.SuppressTriggersKey] = true,
            });

        result.Errors.Should().BeNullOrEmpty("a workflow-triggered write must not be rolled back");
        (await CountAsync("orders", "id = 1 AND status = 'shipped'")).Should().Be(1);

        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle("the workflow's change belongs in the trail like any other");
        JsonArray(rows[0].ChangedColumns).Should().Equal("status");
    }

    [Fact]
    public async Task Batch_RecordsEveryAction_InsertAndUpdate()
    {
        // The batch path runs both hook phases per action, so each row of a batch carries
        // its own before-image and lands in the trail like a single-row write.
        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [ { insert: { status: \"batched\", total: 3 } }, " +
            "{ update: { id: 1, status: \"shipped\" } } ]) }");

        result.Errors.Should().BeNullOrEmpty();

        var rows = await HistoryRowsAsync();
        rows.Should().HaveCount(2);

        rows[0].Op.Should().Be("insert");
        rows[0].Before.Should().BeNull();
        Json(rows[0].EntityId)["id"].GetInt64().Should().BeGreaterThan(1, "the generated key names the row");

        rows[1].Op.Should().Be("update");
        Json(rows[1].EntityId)["id"].GetInt64().Should().Be(1);
        Json(rows[1].Before)["status"].GetString().Should().Be("packing",
            "each action's before-image is its own — the insert's must not leak into the update's");
        Json(rows[1].After)["status"].GetString().Should().Be("shipped");
        JsonArray(rows[1].ChangedColumns).Should().Equal("status");
    }

    [Fact]
    public async Task FailedBatchAction_RollsBackEveryHistoryRowInTheBatch()
    {
        // The first action commits its history row inside the transaction; the second is
        // rejected by the CHECK constraint. Both the data and the whole trail roll back.
        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [ { update: { id: 1, status: \"shipped\" } }, " +
            "{ insert: { status: \"boom\", total: 1 } } ]) }");

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("orders", "id = 1 AND status = 'packing'")).Should().Be(1, "the data rolled back");
        (await HistoryRowsAsync()).Should().BeEmpty("no trail for a batch that never committed");
    }

    [Fact]
    public async Task TreeSync_RecordsEveryOperationInTheTree()
    {
        // A nested sync writes the parent and its children in one transaction; each
        // operation records its own history row on that same transaction.
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec("DROP TABLE IF EXISTS blogs");
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL REFERENCES blogs(id),
                title TEXT NULL
            )
            """);
        var model = await LoadModelAsync(
            "main.blogs { history: enabled }",
            "main.posts { history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" } ] }) }", model);

        result.Errors.Should().BeNullOrEmpty();

        var rows = await HistoryRowsAsync();
        rows.Should().HaveCount(2);
        rows[0].Entity.Should().Be("main.blogs");
        rows[0].Op.Should().Be("insert");
        rows[1].Entity.Should().Be("main.posts");
        // The child's recorded row must carry the parent key the sync generated for it.
        Json(rows[1].After)["blog_id"].GetInt64().Should().Be(Json(rows[0].EntityId)["id"].GetInt64());
    }

    [Fact]
    public async Task NonHistoryTable_RecordsNothing()
    {
        await Exec("DROP TABLE IF EXISTS gadgets");
        await Exec("CREATE TABLE gadgets (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync("mutation { gadgets(insert: { label: \"x\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("gadgets", "label = 'x'")).Should().Be(1);
        (await HistoryRowsAsync()).Should().BeEmpty("gadgets does not opt into history");
    }

    [Fact]
    public async Task AllColumnsTracked_RecordsEveryColumnOfTheRow()
    {
        // No history-columns → every column is tracked, including the untracked-in-the-
        // default-fixture 'note'.
        var model = await LoadModelAsync(
            "main.orders { history: update }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, note: \"edited\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        JsonArray(rows[0].ChangedColumns).Should().Equal("note");
        Json(rows[0].Before).Keys.Should().BeEquivalentTo(new[] { "id", "status", "total", "note" });
        Json(rows[0].Before)["note"].GetString().Should().Be("first");
        Json(rows[0].After)["note"].GetString().Should().Be("edited");
    }

    [Fact]
    public async Task BatchUpsert_InsertOnlyConfig_NewKey_RecordsTheInsert()
    {
        // The batch upsert is driven through the pipeline as an update, but inserting a
        // new row is an insert — a table recording only inserts must not silently skip it.
        var model = await LoadModelAsync(
            "main.orders { history: insert }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [ { upsert: { id: 77, status: \"fresh\" } } ]) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "id = 77 AND status = 'fresh'")).Should().Be(1);

        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle("an upsert that inserted is an insert this table records");
        rows[0].Op.Should().Be("insert");
        rows[0].Before.Should().BeNull();
        Json(rows[0].EntityId)["id"].GetInt64().Should().Be(77);
        Json(rows[0].After)["status"].GetString().Should().Be("fresh");
    }

    [Fact]
    public async Task BatchUpsert_InsertOnlyConfig_ExistingKey_RecordsNothing()
    {
        // The same upsert against an existing row is an update, which this table does
        // not record.
        var model = await LoadModelAsync(
            "main.orders { history: insert }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [ { upsert: { id: 1, status: \"shipped\" } } ]) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "id = 1 AND status = 'shipped'")).Should().Be(1, "the data change still happened");
        (await HistoryRowsAsync()).Should().BeEmpty("the actual operation was an update, which is not opted in");
    }

    [Fact]
    public async Task BatchUpsert_UpdateOnlyConfig_NewKey_RecordsNothing()
    {
        // An upsert that inserts a new row is an insert; a table recording only updates
        // never opted into it.
        var model = await LoadModelAsync(
            "main.orders { history: update }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [ { upsert: { id: 88, status: \"fresh\" } } ]) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "id = 88 AND status = 'fresh'")).Should().Be(1, "the row was still inserted");
        (await HistoryRowsAsync()).Should().BeEmpty("the actual operation was an insert, which is not opted in");
    }

    [Fact]
    public async Task BatchUpsert_UpdateOnlyConfig_ExistingKey_RecordsTheUpdate()
    {
        var model = await LoadModelAsync(
            "main.orders { history: update }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [ { upsert: { id: 1, status: \"shipped\" } } ]) }", model);

        result.Errors.Should().BeNullOrEmpty();

        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Op.Should().Be("update");
        Json(rows[0].EntityId)["id"].GetInt64().Should().Be(1);
        Json(rows[0].Before)["status"].GetString().Should().Be("packing");
        Json(rows[0].After)["status"].GetString().Should().Be("shipped");
        JsonArray(rows[0].ChangedColumns).Should().Equal("status");
    }

    [Fact]
    public async Task MiscasedHistoryColumns_TrailJsonKeysCarryDbCasing()
    {
        // history-columns entries are canonicalized to the database column casing at
        // parse time, so the trail's JSON keys (and the read-back identifiers) carry
        // the real column names, not the config's casing.
        var model = await LoadModelAsync(
            "main.orders { history: enabled; history-columns: STATUS,Total }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        Json(rows[0].Before).Keys.Should().BeEquivalentTo(new[] { "status", "total" });
        Json(rows[0].After).Keys.Should().BeEquivalentTo(new[] { "status", "total" });
        JsonArray(rows[0].ChangedColumns).Should().Equal("status");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(
        string mutation,
        IDbModel? model = null,
        IDictionary<string, object?>? userContext = null)
    {
        model ??= _model;
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        // Register the built-in history writer exactly as the host DI does: one instance
        // surfaced under both in-transaction hook phases.
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = userContext ?? new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }
}

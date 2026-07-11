using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Cdc;
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
/// End-to-end proof of the CDC transactional outbox (slice 2). A widget table
/// opts into emit-events; the built-in <see cref="OutboxMutationHook"/> writes an
/// event row into the model's outbox table in the SAME transaction as each data
/// change. The critical guarantee is atomicity: when the data write is rejected
/// by the database (a CHECK violation) BOTH the data change and its outbox row
/// roll back together — no event is emitted for a change that never committed.
/// </summary>
public sealed class OutboxMutationHookTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_outbox_hook_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS widgets");
        await Exec("DROP TABLE IF EXISTS __outbox");
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL CHECK (name <> 'boom')
            )
            """);
        // The transactional outbox, matching the documented column contract.
        await Exec(
            """
            CREATE TABLE __outbox (
                id            INTEGER PRIMARY KEY,
                aggregate     TEXT NOT NULL,
                op            TEXT NOT NULL,
                payload       TEXT NOT NULL,
                tenant        TEXT NULL,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                dispatched_at TEXT NULL,
                attempts      INTEGER NOT NULL DEFAULT 0,
                dead          INTEGER NOT NULL DEFAULT 0
            )
            """);
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'original')");

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "main.widgets { emit-events: insert,update,delete; event-payload: changed }",
            ":root { outbox-table: main.__outbox }",
        }));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

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

    private async Task<List<(string aggregate, string op, string payload, string? tenant)>> OutboxRowsAsync()
    {
        var rows = new List<(string, string, string, string?)>();
        await using var cmd = new SqliteCommand(
            "SELECT aggregate, op, payload, tenant FROM __outbox ORDER BY id", _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    [Fact]
    public async Task Insert_WritesOneOutboxRow_WithChangedPayload()
    {
        var result = await ExecuteMutationAsync("mutation { widgets(insert: { name: \"allowed\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("widgets", "name = 'allowed'")).Should().Be(1);

        var rows = await OutboxRowsAsync();
        rows.Should().ContainSingle();
        rows[0].aggregate.Should().Be("main.widgets");
        rows[0].op.Should().Be("insert");
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rows[0].payload)!;
        payload.Should().ContainKey("name");
        // The database-generated identity must be captured — the client did not supply
        // id, so it comes from the insert's returned identity (result), not the input.
        payload.Should().ContainKey("id", "the generated primary key identifies the row");
        payload["id"].GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Insert_KeysPayload_CapturesGeneratedIdentity()
    {
        // event-payload: keys with a DB-generated PK — the payload must be exactly the
        // key, sourced from the insert's identity result (the regression this slice fixes).
        var model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[]
        {
            "main.widgets { emit-events: insert; event-payload: keys }",
            ":root { outbox-table: main.__outbox }",
        })).LoadAsync();

        var result = await ExecuteMutationAsync("mutation { widgets(insert: { name: \"k\" }) }", model);
        result.Errors.Should().BeNullOrEmpty();

        var rows = await OutboxRowsAsync();
        rows.Should().ContainSingle();
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rows[0].payload)!;
        payload.Keys.Should().BeEquivalentTo(new[] { "id" }, "keys payload is the primary key only");
        payload["id"].GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Delete_WritesOutboxRow_OpDelete()
    {
        var result = await ExecuteMutationAsync("mutation { widgets(delete: { id: 1 }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("widgets", "id = 1")).Should().Be(0);

        var rows = await OutboxRowsAsync();
        rows.Should().ContainSingle();
        rows[0].op.Should().Be("delete");
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rows[0].payload)!
            .Should().ContainKey("id", "the delete predicate carries the primary key");
    }

    [Fact]
    public async Task FailedWrite_RollsBackDataAndOutbox_Atomically()
    {
        // The hook passes and writes its outbox row inside the transaction, then the
        // CHECK constraint rejects name='boom'. The whole transaction must roll back:
        // neither the widget nor the outbox event survives — the exactly-once guarantee.
        var result = await ExecuteMutationAsync("mutation { widgets(insert: { name: \"boom\" }) }");

        result.Errors.Should().NotBeNullOrEmpty("the CHECK violation aborts the transaction");
        (await CountAsync("widgets", "name = 'boom'")).Should().Be(0, "the data write rolled back");
        (await OutboxRowsAsync()).Should().BeEmpty(
            "the outbox row must roll back with the data change — no event for an uncommitted write");
    }

    [Fact]
    public async Task NonEmittingTable_WritesNoOutboxRow()
    {
        // Create a second table with no emit-events metadata; a write to it must not
        // produce an outbox row (the hook no-ops for non-CDC tables).
        await Exec("DROP TABLE IF EXISTS gadgets");
        await Exec("CREATE TABLE gadgets (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        var factory = new SqliteDbConnFactory(ConnString);
        var model = await new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "main.widgets { emit-events: insert,update,delete }",
            ":root { outbox-table: main.__outbox }",
        })).LoadAsync();

        var result = await ExecuteMutationAsync("mutation { gadgets(insert: { label: \"x\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("gadgets", "label = 'x'")).Should().Be(1);
        (await OutboxRowsAsync()).Should().BeEmpty("gadgets does not opt into events");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation, IDbModel? model = null)
    {
        model ??= _model;
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        // Register the built-in CDC outbox writer exactly as the host DI does: an
        // after-write, in-transaction hook.
        services.AddSingleton<IInTransactionMutationHook, OutboxMutationHook>();
        services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }
}

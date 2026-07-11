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
/// Proves the CDC transactional outbox now covers the batch and TreeSync (nested)
/// write paths, not just single-row mutations (CDC slice 3). Each batch action and
/// each tree-sync operation writes its event on the same transaction as the data
/// change, and a generated primary key is captured on inserts. Batch atomicity is
/// verified: a rejected action rolls back every event in the batch.
/// </summary>
public sealed class OutboxBatchTreeSyncTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_outbox_batch_tree_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[] { "posts", "blogs", "widgets", "__outbox" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL CHECK (name <> 'boom'))");
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL REFERENCES blogs(id),
                title TEXT NOT NULL
            )
            """);
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
        _model = await new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "main.widgets { emit-events: insert,update,delete }",
            "main.blogs { emit-events: insert,update,delete }",
            "main.posts { emit-events: insert,update,delete }",
            ":root { outbox-table: main.__outbox }",
        })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<(string aggregate, string op, string payload)>> OutboxRowsAsync()
    {
        var rows = new List<(string, string, string)>();
        await using var cmd = new SqliteCommand("SELECT aggregate, op, payload FROM __outbox ORDER BY id", _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static Dictionary<string, JsonElement> Payload(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private async Task<long> CountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Batch_EmitsAnEventPerAction_WithGeneratedInsertKeys()
    {
        var result = await ExecuteMutationAsync(
            "mutation { widgets_batch(actions: [ { insert: { name: \"a\" } }, { insert: { name: \"b\" } }, { update: { id: 1, name: \"x\" } } ]) }");

        result.Errors.Should().BeNullOrEmpty();

        var rows = await OutboxRowsAsync();
        rows.Should().HaveCount(3);
        rows.Select(r => r.op).Should().Equal("insert", "insert", "update");
        rows.Should().OnlyContain(r => r.aggregate == "main.widgets");
        // Both inserts captured their generated identity.
        Payload(rows[0].payload)["id"].GetInt64().Should().BeGreaterThan(0);
        Payload(rows[1].payload)["id"].GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Batch_FailedAction_RollsBackEveryEvent()
    {
        // The second action violates the CHECK constraint. The whole batch transaction
        // rolls back, so neither the data nor ANY of the batch's outbox events survive.
        var result = await ExecuteMutationAsync(
            "mutation { widgets_batch(actions: [ { insert: { name: \"ok\" } }, { insert: { name: \"boom\" } } ]) }");

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("widgets", "name = 'ok'")).Should().Be(0, "the batch rolled back");
        (await OutboxRowsAsync()).Should().BeEmpty("every event in a rolled-back batch is discarded");
    }

    [Fact]
    public async Task TreeSync_EmitsAnEventPerOperation_WithGeneratedKeysAndResolvedFk()
    {
        // Sync a new blog with one nested post: root insert (blog) + child insert (post).
        // Both are inserts on generated-key tables, so both events must carry the
        // generated id; the post's blog_id must be the blog's generated id.
        var result = await ExecuteMutationAsync(
            "mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" } ] }) }");

        result.Errors.Should().BeNullOrEmpty();

        var rows = await OutboxRowsAsync();
        rows.Should().HaveCount(2);

        var blogEvent = rows.Single(r => r.aggregate == "main.blogs");
        var postEvent = rows.Single(r => r.aggregate == "main.posts");
        blogEvent.op.Should().Be("insert");
        postEvent.op.Should().Be("insert");

        var blogId = Payload(blogEvent.payload)["id"].GetInt64();
        blogId.Should().BeGreaterThan(0);
        Payload(postEvent.payload)["id"].GetInt64().Should().BeGreaterThan(0);
        Payload(postEvent.payload)["blog_id"].GetInt64().Should().Be(blogId,
            "the child event captures the parent key resolved during the sync");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
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
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }
}

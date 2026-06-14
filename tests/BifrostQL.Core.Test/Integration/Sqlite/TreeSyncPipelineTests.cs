using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
/// End-to-end proof that tree-sync mutations now route every inferred operation
/// (nested insert/update + orphan delete) through the mutation-transformer
/// pipeline — so soft-delete, audit-populate, and policy apply to the nested path
/// exactly as they do to a single-row mutation.
///
/// Covers:
///  - Orphaning a child on a SOFT-DELETE table soft-deletes it (row stays, the
///    soft-delete column is stamped) instead of physically removing it.
///  - Orphaning a child on a NON-soft-delete table still hard-DELETEs it
///    (behavior preserved when no transformer applies).
///  - Audit columns populate on a tree-sync insert and update once a
///    populate-tagged column + user-audit-key are configured (the pipeline runs).
/// </summary>
public sealed class TreeSyncPipelineTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_treesync_pipeline_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec("DROP TABLE IF EXISTS blogs");
        await Exec("DROP TABLE IF EXISTS items");

        await Exec(
            """
            CREATE TABLE blogs (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                deleted_at TEXT,
                FOREIGN KEY (blog_id) REFERENCES blogs(id)
            )
            """);
        await Exec("INSERT INTO blogs(id, name) VALUES (1, 'A')");
        // Blog 1: post 1 (kept) and post 2 (will be orphaned), both live.
        await Exec(
            """
            INSERT INTO posts(id, blog_id, title, deleted_at) VALUES
                (1, 1, 'keep', NULL),
                (2, 1, 'orphan', NULL)
            """);

        // Audit table: created_* set on insert, updated_* on insert+update.
        await Exec(
            """
            CREATE TABLE items (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                created_at TEXT,
                created_by TEXT,
                updated_at TEXT
            )
            """);
        await Exec("INSERT INTO items(id, name, created_at, created_by, updated_at) VALUES (1, 'old', NULL, NULL, NULL)");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<IDbModel> LoadModelAsync(params string[] rules)
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(rules));
        return await loader.LoadAsync();
    }

    [Fact]
    public async Task Sync_OrphanOnSoftDeleteTable_SoftDeletesInsteadOfHardDelete()
    {
        var model = await LoadModelAsync("*.posts { soft-delete: deleted_at }");

        // Submit blog 1 with only post 1 — post 2 becomes an orphan.
        var result = await ExecuteMutationAsync(
            model,
            "mutation { blogs(sync: { id: 1, name: \"A\", posts: [{ id: 1, title: \"keep\" }] }) }",
            new[] { new SoftDeleteMutationTransformer() });

        result.Errors.Should().BeNullOrEmpty();

        // Orphan post 2 is soft-deleted, not removed: the row survives with deleted_at stamped.
        (await CountAsync("SELECT COUNT(*) FROM posts WHERE id = 2")).Should().Be(1,
            "soft-delete keeps the orphaned row");
        (await CountAsync("SELECT COUNT(*) FROM posts WHERE id = 2 AND deleted_at IS NOT NULL")).Should().Be(1,
            "the orphan delete was rewritten to a soft-delete UPDATE that stamped deleted_at");
        // The kept child is untouched.
        (await CountAsync("SELECT COUNT(*) FROM posts WHERE id = 1 AND deleted_at IS NULL")).Should().Be(1);
    }

    [Fact]
    public async Task Sync_OrphanOnNonSoftDeleteTable_HardDeletes()
    {
        // No soft-delete metadata: the transformer does not apply, so the orphan
        // delete stays a physical DELETE — behavior preserved.
        var model = await LoadModelAsync();

        var result = await ExecuteMutationAsync(
            model,
            "mutation { blogs(sync: { id: 1, name: \"A\", posts: [{ id: 1, title: \"keep\" }] }) }",
            new[] { new SoftDeleteMutationTransformer() });

        result.Errors.Should().BeNullOrEmpty();

        // Orphan post 2 is physically gone.
        (await CountAsync("SELECT COUNT(*) FROM posts WHERE id = 2")).Should().Be(0,
            "without soft-delete metadata the orphan is hard-deleted");
        (await CountAsync("SELECT COUNT(*) FROM posts WHERE id = 1")).Should().Be(1,
            "the kept child remains");
    }

    [Fact]
    public async Task Sync_Insert_PopulatesAuditColumns()
    {
        var model = await LoadModelAsync(
            ":root { user-audit-key: id }",
            "*.items.created_at { populate: created-on }",
            "*.items.created_by { populate: created-by }",
            "*.items.updated_at { populate: updated-on }");

        // Fresh insert (no PK) of an item — audit columns must be stamped by the pipeline.
        var result = await ExecuteMutationAsync(
            model,
            "mutation { items(sync: { name: \"new item\" }) }",
            new[] { new AuditMutationTransformer() },
            userContext: new Dictionary<string, object?> { ["id"] = "user-42" });

        result.Errors.Should().BeNullOrEmpty();

        var row = await ScalarAsync("SELECT COUNT(*) FROM items WHERE name = 'new item' AND created_at IS NOT NULL AND updated_at IS NOT NULL AND created_by = 'user-42'");
        Convert.ToInt64(row).Should().Be(1,
            "the audit transformer ran on the tree-sync insert and stamped created-on/created-by/updated-on");
    }

    [Fact]
    public async Task Sync_Update_PopulatesUpdatedAuditColumn()
    {
        var model = await LoadModelAsync(
            ":root { user-audit-key: id }",
            "*.items.updated_at { populate: updated-on }");

        // Update existing item 1 (PK present + a changed column) via tree-sync.
        var result = await ExecuteMutationAsync(
            model,
            "mutation { items(sync: { id: 1, name: \"renamed\" }) }",
            new[] { new AuditMutationTransformer() },
            userContext: new Dictionary<string, object?> { ["id"] = "user-42" });

        result.Errors.Should().BeNullOrEmpty();

        (await CountAsync("SELECT COUNT(*) FROM items WHERE id = 1 AND name = 'renamed' AND updated_at IS NOT NULL")).Should().Be(1,
            "the audit transformer ran on the tree-sync update and stamped updated-on");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(
        IDbModel model,
        string mutation,
        IMutationTransformer[] transformers,
        IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = transformers,
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            if (userContext != null)
                options.UserContext = userContext;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }
}

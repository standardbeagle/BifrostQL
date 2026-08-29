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
/// End-to-end coverage for the explicit-ops graph save (<c>save:</c>) against real SQLite:
/// a mixed document (root update, child insert/update/delete) applies in ONE transaction,
/// unlisted children are untouched (no orphan inference — that is sync's job), a fresh
/// root's children resolve their FK from the generated parent key, root delete works and
/// returns the key, and a soft-delete table's <c>_op: delete</c> is rewritten to an UPDATE
/// by the transformer chain exactly like every other write path.
/// </summary>
public sealed class SaveMutationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_save_mutation_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var drop in new[] { "posts", "blogs" })
            await Exec($"DROP TABLE IF EXISTS {drop}");
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL, deleted_at TEXT NULL)");
        await Exec("""
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL REFERENCES blogs(id),
                title TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO blogs(id, name) VALUES (1, 'main')");
        await Exec("INSERT INTO posts(id, blog_id, title) VALUES (10, 1, 'keep me'), (11, 1, 'edit me'), (12, 1, 'delete me')");
        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<ExecutionResult> ExecuteAsync(string mutation, IMutationTransformer[]? transformers = null)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = transformers ?? Array.Empty<IMutationTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });
    }

    [Fact]
    public async Task Save_MixedOpsGraph_AppliesEverything_UnlistedChildrenUntouched()
    {
        var result = await ExecuteAsync("""
            mutation { blogs(save: {
                id: 1, name: "renamed",
                posts: [
                    { title: "brand new" },
                    { id: 11, title: "edited" },
                    { id: 12, _op: delete }
                ]
            }) }
            """);

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        (await ScalarAsync("SELECT name FROM blogs WHERE id = 1")).Should().Be("renamed");
        (await ScalarAsync("SELECT COUNT(*) FROM posts WHERE title = 'brand new' AND blog_id = 1")).Should().Be(1L);
        (await ScalarAsync("SELECT title FROM posts WHERE id = 11")).Should().Be("edited");
        (await ScalarAsync("SELECT COUNT(*) FROM posts WHERE id = 12")).Should().Be(0L);
        (await ScalarAsync("SELECT title FROM posts WHERE id = 10")).Should().Be("keep me",
            "an UNLISTED child must be untouched — save never infers orphan deletes");
    }

    [Fact]
    public async Task Save_FreshRootWithChildren_ResolvesGeneratedForeignKeys()
    {
        var result = await ExecuteAsync("""
            mutation { blogs(save: {
                name: "fresh",
                posts: [ { title: "a" }, { title: "b" } ]
            }) }
            """);

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        var blogId = await ScalarAsync("SELECT id FROM blogs WHERE name = 'fresh'");
        blogId.Should().NotBeNull();
        (await ScalarAsync($"SELECT COUNT(*) FROM posts WHERE blog_id = {blogId} AND title IN ('a','b')")).Should().Be(2L);
    }

    [Fact]
    public async Task Save_RootDelete_RemovesRowAndReturnsKey()
    {
        await Exec("DELETE FROM posts WHERE blog_id = 1");
        var result = await ExecuteAsync("mutation { blogs(save: { id: 1, _op: delete }) }");

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        (await ScalarAsync("SELECT COUNT(*) FROM blogs WHERE id = 1")).Should().Be(0L);
        var json = new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result);
        System.Text.Json.JsonDocument.Parse(json).RootElement
            .GetProperty("data").GetProperty("blogs").GetInt32().Should().Be(1, "the submitted root key is returned");
    }

    [Fact]
    public async Task Save_SoftDeleteTable_OpDelete_IsRewrittenToUpdate()
    {
        _model.GetTableFromDbName("blogs").Metadata[MetadataKeys.SoftDelete.Column] = "deleted_at";
        try
        {
            await Exec("DELETE FROM posts WHERE blog_id = 1");
            var result = await ExecuteAsync("mutation { blogs(save: { id: 1, _op: delete }) }",
                transformers: new IMutationTransformer[] { new SoftDeleteMutationTransformer() });

            result.Errors.Should().BeNullOrEmpty(
                $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
            (await ScalarAsync("SELECT COUNT(*) FROM blogs WHERE id = 1")).Should().Be(1L, "the row survives");
            (await ScalarAsync("SELECT COUNT(*) FROM blogs WHERE id = 1 AND deleted_at IS NOT NULL")).Should().Be(1L,
                "the transformer chain rewrote the explicit delete to a soft-delete UPDATE");
        }
        finally
        {
            _model.GetTableFromDbName("blogs").Metadata.Remove(MetadataKeys.SoftDelete.Column);
        }
    }

    [Fact]
    public async Task Save_FailureAnywhere_RollsBackTheWholeGraph()
    {
        // The child insert violates NOT NULL (no title): the root rename must roll back with it.
        var result = await ExecuteAsync("""
            mutation { blogs(save: {
                id: 1, name: "should not stick",
                posts: [ { blog_id: 1 } ]
            }) }
            """);

        result.Errors.Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT name FROM blogs WHERE id = 1")).Should().Be("main", "one graph, one transaction");
    }
}

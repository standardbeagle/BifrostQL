using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Proves the soft-delete query arguments (_includeDeleted / _onlyDeleted) are
/// honored on nested multi-link fields, not just the root field: the schema
/// emits them on the nested collection, and supplying _includeDeleted:true on a
/// nested field surfaces that child table's soft-deleted rows while leaving the
/// default nested query filtered to live rows only.
/// </summary>
public sealed class NestedSoftDeleteTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_nested_softdelete_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec("DROP TABLE IF EXISTS blogs");
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
        // Blog 1: post 1 live, post 2 soft-deleted.
        await Exec(
            """
            INSERT INTO posts(id, blog_id, title, deleted_at) VALUES
                (1, 1, 'live', NULL),
                (2, 1, 'deleted', '2026-01-01T00:00:00Z')
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "*.posts { soft-delete: deleted_at }",
        }));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public void Schema_NestedMultiLink_SoftDeleteTable_EmitsIncludeDeletedArg()
    {
        var blogs = _model.GetTableFromDbName("blogs");
        var sdl = new TableSchemaGenerator(blogs).GetTableTypeDefinition(_model, includeDynamicJoins: false);

        // The nested posts collection must surface the soft-delete query args.
        sdl.Should().Contain("_includeDeleted: Boolean");
        sdl.Should().Contain("_onlyDeleted: Boolean");
        sdl.Should().Contain("posts(filter: TableFilterpostsInput, limit: Int, offset: Int, sort: [postsSortEnum!] _includeDeleted: Boolean _onlyDeleted: Boolean) : posts_paged");
    }

    [Fact]
    public async Task NestedQuery_Default_ExcludesSoftDeletedChildren()
    {
        var postIds = await ExecutePostIdsAsync("posts(sort: [id_asc])");

        postIds.Should().Equal(new[] { 1 }, "the default nested query filters out soft-deleted rows");
    }

    [Fact]
    public async Task NestedQuery_IncludeDeletedTrue_ReturnsSoftDeletedChildren()
    {
        var postIds = await ExecutePostIdsAsync("posts(sort: [id_asc], _includeDeleted: true)");

        postIds.Should().Equal(new[] { 1, 2 },
            "_includeDeleted:true on the nested field surfaces soft-deleted child rows");
    }

    private async Task<List<int>> ExecutePostIdsAsync(string postsSelection)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new SoftDeleteFilterTransformer() },
        };
        var execManager = new SqlExecutionManager(_model, schema, new QueryTransformerService(filterTransformers));

        var executor = new DocumentExecuter();
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query =
                $$"""
                {
                  blogs {
                    data {
                      id
                      {{postsSelection}} {
                        data { id title }
                      }
                    }
                  }
                }
                """;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = execManager,
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(execution);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("blogs")
            .GetProperty("data")
            .EnumerateArray()
            .First()
            .GetProperty("posts")
            .GetProperty("data")
            .EnumerateArray()
            .Select(p => p.GetProperty("id").GetInt32())
            .ToList();
    }
}

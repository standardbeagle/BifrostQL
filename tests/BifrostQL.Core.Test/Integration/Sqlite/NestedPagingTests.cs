using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Proves the per-parent paged contract for nested multi-link collections:
/// the schema emits a `<child>_paged` field with limit/offset/sort args, and a
/// nested query with `limit:1` returns at most one child per parent while the
/// per-parent `total` reflects that parent's full child count (not the global
/// count, and not bled across parents).
/// </summary>
public sealed class NestedPagingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_nested_paging_test;Mode=Memory;Cache=Shared";
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
                FOREIGN KEY (blog_id) REFERENCES blogs(id)
            )
            """);
        // Blog A (id 1) has 3 posts; Blog B (id 2) has 2 posts.
        await Exec(
            """
            INSERT INTO blogs(id, name) VALUES (1, 'A'), (2, 'B')
            """);
        await Exec(
            """
            INSERT INTO posts(id, blog_id, title) VALUES
                (1, 1, 'a1'), (2, 1, 'a2'), (3, 1, 'a3'),
                (4, 2, 'b1'), (5, 2, 'b2')
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public void Schema_NestedMultiLink_EmitsPagedFieldWithPagingArgs()
    {
        // The posts collection on blogs is a multi-link, so it must surface the
        // paged wrapper plus limit/offset/sort args.
        var blogs = _model.GetTableFromDbName("blogs");
        var sdl = new TableSchemaGenerator(blogs).GetTableTypeDefinition(_model, includeDynamicJoins: false);

        sdl.Should().Contain("posts(filter: TableFilterpostsInput, limit: Int, offset: Int, sort: [postsSortEnum!]) : posts_paged");
    }

    [Fact]
    public async Task NestedQuery_PerParentLimit_IsolatesParentsAndReportsPerParentTotal()
    {
        var schema = DbSchema.FromModel(_model);
        var executor = new DocumentExecuter();
        var factory = new SqliteDbConnFactory(ConnString);
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query =
                """
                {
                  blogs(sort: [id_asc]) {
                    data {
                      id
                      posts(limit: 1, sort: [id_asc]) {
                        total
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
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(execution);
        using var doc = JsonDocument.Parse(json);
        var blogRows = doc.RootElement
            .GetProperty("data")
            .GetProperty("blogs")
            .GetProperty("data")
            .EnumerateArray()
            .ToList();

        blogRows.Should().HaveCount(2);

        // Blog A: per-parent limit of 1 yields exactly one post, total is its
        // own count (3) — NOT the global 5, and NOT bled from blog B.
        var blogA = blogRows[0].GetProperty("posts");
        blogA.GetProperty("total").GetInt32().Should().Be(3);
        var blogAPosts = blogA.GetProperty("data").EnumerateArray().ToList();
        blogAPosts.Should().ContainSingle();
        blogAPosts[0].GetProperty("id").GetInt32().Should().Be(1);

        // Blog B: limit of 1 yields one post, total is its own count (2).
        var blogB = blogRows[1].GetProperty("posts");
        blogB.GetProperty("total").GetInt32().Should().Be(2);
        var blogBPosts = blogB.GetProperty("data").EnumerateArray().ToList();
        blogBPosts.Should().ContainSingle();
        blogBPosts[0].GetProperty("id").GetInt32().Should().Be(4);
    }
}

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
/// Proves many-to-many target collections carry the same per-parent paged
/// contract as multi-links. The schema emits a `<target>_paged` field with
/// limit/offset/sort args, and a nested `limit:1` query returns at most one
/// target per source while the per-parent `total` reflects that source's own
/// junction count — bridged through the junction table without bleeding rows
/// across sources.
/// </summary>
public sealed class ManyToManyPagingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_m2m_paging_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS post_tags");
        await Exec("DROP TABLE IF EXISTS tags");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE tags (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        // Pure junction (two FKs, no payload) -> auto-detected as many-to-many.
        await Exec(
            """
            CREATE TABLE post_tags (
                post_id INTEGER NOT NULL,
                tag_id INTEGER NOT NULL,
                PRIMARY KEY (post_id, tag_id),
                FOREIGN KEY (post_id) REFERENCES posts(id),
                FOREIGN KEY (tag_id) REFERENCES tags(id)
            )
            """);
        await Exec("INSERT INTO posts(id, title) VALUES (1, 'p1'), (2, 'p2')");
        await Exec("INSERT INTO tags(id, name) VALUES (1, 't1'), (2, 't2'), (3, 't3')");
        // Post 1 has 3 tags; Post 2 has 2 tags.
        await Exec(
            """
            INSERT INTO post_tags(post_id, tag_id) VALUES
                (1, 1), (1, 2), (1, 3),
                (2, 1), (2, 2)
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
    public void Schema_ManyToMany_EmitsPagedFieldWithPagingArgs()
    {
        var posts = _model.GetTableFromDbName("posts");
        // Sanity: the junction was detected as a many-to-many bridge.
        posts.ManyToManyLinks.Should().ContainKey("tags");

        var sdl = new TableSchemaGenerator(posts).GetTableTypeDefinition(_model, includeDynamicJoins: false);

        sdl.Should().Contain("tags(filter: TableFiltertagsInput, limit: Int, offset: Int, sort: [tagsSortEnum!]) : tags_paged");
    }

    [Fact]
    public async Task ManyToMany_PerParentLimit_IsolatesSourcesAndReportsPerParentTotal()
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
                  posts(sort: [id_asc]) {
                    data {
                      id
                      tags(limit: 1, sort: [id_asc]) {
                        total
                        data { id name }
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
        var postRows = doc.RootElement
            .GetProperty("data")
            .GetProperty("posts")
            .GetProperty("data")
            .EnumerateArray()
            .ToList();

        postRows.Should().HaveCount(2);

        // Post 1: limit of 1 yields exactly one tag, total is its own junction
        // count (3) — NOT the global tag count, and NOT bled from post 2.
        var post1 = postRows[0].GetProperty("tags");
        post1.GetProperty("total").GetInt32().Should().Be(3);
        var post1Tags = post1.GetProperty("data").EnumerateArray().ToList();
        post1Tags.Should().ContainSingle();
        post1Tags[0].GetProperty("id").GetInt32().Should().Be(1);

        // Post 2: limit of 1 yields one tag, total is its own count (2).
        var post2 = postRows[1].GetProperty("tags");
        post2.GetProperty("total").GetInt32().Should().Be(2);
        var post2Tags = post2.GetProperty("data").EnumerateArray().ToList();
        post2Tags.Should().ContainSingle();
        post2Tags[0].GetProperty("id").GetInt32().Should().Be(1);
    }
}

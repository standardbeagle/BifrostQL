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
/// Finding M9 integration: a parent table with more rows than the default page (250
/// blogs, no explicit limit) queried with a nested collection must return the default
/// page of parents (100) with each parent's children correctly attached — the same
/// result as before the fix, now produced by a child join-id query that is paged to
/// the parent's window instead of spanning all 250 parents. The SQL-text half of this
/// contract lives in DefaultPageJoinAlignmentTests.
/// </summary>
public sealed class NestedCollectionDefaultPageTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_nested_default_page_test;Mode=Memory;Cache=Shared";
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

        // 250 parents — more than the 100-row default page — each with one child.
        var blogValues = string.Join(", ", Enumerable.Range(1, 250).Select(i => $"({i}, 'blog{i}')"));
        await Exec($"INSERT INTO blogs(id, name) VALUES {blogValues}");
        var postValues = string.Join(", ", Enumerable.Range(1, 250).Select(i => $"({i}, {i}, 'post{i}')"));
        await Exec($"INSERT INTO posts(id, blog_id, title) VALUES {postValues}");

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
    public async Task NestedCollection_ParentBeyondDefaultPage_ReturnsDefaultPageWithChildrenAttached()
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
                      posts { data { id title } }
                    }
                  }
                }
                """;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(execution);
        using var doc = JsonDocument.Parse(json);

        var data = doc.RootElement.GetProperty("data").GetProperty("blogs").GetProperty("data");

        // The default page bounds the parent set to 100 of the 250 rows.
        data.GetArrayLength().Should().Be(100, "no explicit limit pages the parent to the dialect default window");

        // Every returned parent has its own child attached — the restricted join-id
        // query pages with the SAME window as the parent, so no parent on the page
        // resolves an empty collection.
        var ids = data.EnumerateArray().Select(b => b.GetProperty("id").GetInt32()).ToList();
        ids.Should().Equal(Enumerable.Range(1, 100));
        foreach (var blog in data.EnumerateArray())
        {
            var posts = blog.GetProperty("posts").GetProperty("data");
            posts.GetArrayLength().Should().Be(1, $"blog {blog.GetProperty("id").GetInt32()} has exactly one post");
            posts[0].GetProperty("id").GetInt32().Should().Be(blog.GetProperty("id").GetInt32());
        }
    }
}

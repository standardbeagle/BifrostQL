using System.Text.Json;
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
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// One link selected under two aliases is two joins with two result sets (the join
/// name carries the response key). The reader must hand each alias its own result
/// set: matching a join by link name as well as alias gave the second alias the
/// first alias's rows.
/// </summary>
public sealed class AliasedLinkSelectionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_aliased_link_selection;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS posts",
            "DROP TABLE IF EXISTS authors",
            "CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE posts (id INTEGER PRIMARY KEY, author_id INTEGER NOT NULL REFERENCES authors(id), published INTEGER NOT NULL, title TEXT NOT NULL)",
            "INSERT INTO authors (id, name) VALUES (1, 'ann')",
            "INSERT INTO posts (id, author_id, published, title) VALUES (1, 1, 1, 'live'), (2, 1, 0, 'draft')",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<JsonElement> RunAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
        result.Errors.Should().BeNullOrEmpty();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement.Clone();
    }

    private static string[] Titles(JsonElement author, string key) =>
        author.GetProperty(key).GetProperty("data").EnumerateArray()
            .Select(p => p.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task SameLinkUnderTwoAliases_EachAliasReadsItsOwnRows()
    {
        var root = await RunAsync("""
            { authors { data { id
                live: posts(filter: { published: { _eq: 1 } }) { data { title } }
                drafts: posts(filter: { published: { _eq: 0 } }) { data { title } }
            } } }
            """);

        var author = root.GetProperty("data").GetProperty("authors").GetProperty("data")[0];
        Titles(author, "live").Should().Equal("live");
        Titles(author, "drafts").Should().Equal("draft");
    }

    [Fact]
    public async Task UnaliasedAndAliasedSelectionsOfOneLink_EachReadItsOwnRows()
    {
        var root = await RunAsync("""
            { authors { data { id
                posts(filter: { published: { _eq: 1 } }) { data { title } }
                drafts: posts(filter: { published: { _eq: 0 } }) { data { title } }
            } } }
            """);

        var author = root.GetProperty("data").GetProperty("authors").GetProperty("data")[0];
        Titles(author, "posts").Should().Equal("live");
        Titles(author, "drafts").Should().Equal("draft");
    }

    [Fact]
    public async Task UnaliasedAndAliasedAggregates_EachReadTheirOwnValue()
    {
        // count = 2 rows, sum(published) = 1: distinct values, so a crossed read shows.
        var root = await RunAsync("""
            { authors { data { id
                _agg(operation: Count, value: { posts: { column: id } })
                published: _agg(operation: Sum, value: { posts: { column: published } })
            } } }
            """);

        var author = root.GetProperty("data").GetProperty("authors").GetProperty("data")[0];
        author.GetProperty("_agg").GetDouble().Should().Be(2);
        author.GetProperty("published").GetDouble().Should().Be(1);
    }
}

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

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A fragment spread at the operation root (<c>query { ...F }</c>) names root fields just
/// as writing them inline does. The visitor recorded a spread on the enclosing field, and
/// at the root there is none, so it threw. Every sibling root resolver awaits that one
/// parse, so the whole document failed. Each fact compares the spread form against the
/// inline form of the same selection.
/// </summary>
public sealed class RootFragmentSpreadTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_root_fragment_spread_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS users");
        await Exec("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), amount INTEGER NOT NULL)");
        await Exec("INSERT INTO users(id, name) VALUES (1, 'ann'), (2, 'bob'), (5, 'eve')");
        await Exec("INSERT INTO orders(id, user_id, amount) VALUES (10, 1, 7), (11, 2, 9)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<string> RunAsync(string query, Dictionary<string, object?>? variables = null)
    {
        var schema = DbSchema.FromModel(_model);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?>();
            if (variables != null)
                options.Variables = new Inputs(variables);
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });

        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeFalse(errors.ToString());
        return doc.RootElement.GetProperty("data").GetRawText();
    }

    private const string Inline =
        "{ users(sort: [id_asc]) { data { id name } } o: orders(sort: [id_asc]) { data { id amount } } }";

    [Fact]
    public async Task RootSpread_ReturnsWhatTheInlineFieldsReturn()
    {
        var inline = await RunAsync(Inline);
        inline.Should().Contain("\"eve\"", "control: the inline form reads rows");

        var spread = await RunAsync(
            """
            query { ...Roots }
            fragment Roots on database {
              users(sort: [id_asc]) { data { id name } }
              o: orders(sort: [id_asc]) { data { id amount } }
            }
            """);

        spread.Should().Be(inline);
    }

    [Fact]
    public async Task NestedRootSpread_ReturnsWhatTheInlineFieldsReturn()
    {
        var inline = await RunAsync(Inline);

        var spread = await RunAsync(
            """
            query { ...Outer }
            fragment Outer on database { users(sort: [id_asc]) { data { id name } } ...Inner }
            fragment Inner on database { o: orders(sort: [id_asc]) { data { id amount } } }
            """);

        spread.Should().Be(inline);
    }

    [Fact]
    public async Task RootSpread_MixedWithAnInlineField_ReturnsWhatTheInlineFieldsReturn()
    {
        var inline = await RunAsync(Inline);

        var spread = await RunAsync(
            """
            query { users(sort: [id_asc]) { data { id name } } ...Orders }
            fragment Orders on database { o: orders(sort: [id_asc]) { data { id amount } } }
            """);

        spread.Should().Be(inline);
    }

    [Fact]
    public async Task SkippedRootSpread_ContributesNoField()
    {
        var spread = await RunAsync(
            """
            query Q($skip: Boolean!) { users(sort: [id_asc]) { data { id name } } ...Orders @skip(if: $skip) }
            fragment Orders on database { o: orders(sort: [id_asc]) { data { id amount } } }
            """,
            new Dictionary<string, object?> { ["skip"] = true });

        spread.Should().Be(await RunAsync("{ users(sort: [id_asc]) { data { id name } } }"));
    }
}

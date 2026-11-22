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
/// A table root field must bind the parsed query node with its own response key
/// (alias, else field name) from the operation being executed. An unaliased field
/// used to match any node with the same field name — including an aliased sibling —
/// and the visitor parsed every operation in the document, so another operation's
/// node of the same name could answer for the executing one.
/// </summary>
public sealed class RootFieldNodeBindingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_root_field_node_binding_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS users");
        await Exec(
            """
            CREATE TABLE users (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO users(id, name) VALUES (1, 'ann'), (2, 'bob'), (5, 'eve')");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<JsonElement> RunAsync(string query, string? operationName = null)
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
            options.OperationName = operationName;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static List<(int id, string? name)> Rows(JsonElement data, string responseKey)
        => data.GetProperty(responseKey).GetProperty("data").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetInt32(),
                r.TryGetProperty("name", out var n) ? n.GetString() : null))
            .ToList();

    [Fact]
    public async Task UnaliasedField_BindsItsOwnNode_NotAnAliasedSiblingOfTheSameField()
    {
        var data = await RunAsync(
            "{ a: users(filter: { id: { _eq: 5 } }) { data { id } } users { data { id name } } }");

        Rows(data, "a").Select(r => r.id).Should().Equal(5);
        Rows(data, "users").Should().Equal((1, "ann"), (2, "bob"), (5, "eve"));
    }

    [Fact]
    public async Task AliasedField_ListedAfterTheUnaliasedOne_StillBindsItsOwnNode()
    {
        var data = await RunAsync(
            "{ users { data { id name } } a: users(filter: { id: { _eq: 5 } }) { data { id } } }");

        Rows(data, "users").Should().Equal((1, "ann"), (2, "bob"), (5, "eve"));
        Rows(data, "a").Select(r => r.id).Should().Equal(5);
    }

    [Fact]
    public async Task NamedOperation_BindsNodesFromTheExecutingOperationOnly()
    {
        var data = await RunAsync(
            """
            query A { users(filter: { id: { _eq: 1 } }) { data { id } } }
            query B { users { data { id name } } }
            """,
            operationName: "B");

        Rows(data, "users").Should().Equal((1, "ann"), (2, "bob"), (5, "eve"));
    }
}

using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
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

public sealed class SqliteSelfReferentialGraphQlTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_self_fk_graphql_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await using (var pragma = new SqliteCommand("PRAGMA foreign_keys = ON", _keepAlive))
            await pragma.ExecuteNonQueryAsync();

        await using (var drop = new SqliteCommand("DROP TABLE IF EXISTS categories", _keepAlive))
            await drop.ExecuteNonQueryAsync();

        await using (var create = new SqliteCommand(
            """
            CREATE TABLE categories (
                id INTEGER PRIMARY KEY,
                parent_id INTEGER NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (parent_id) REFERENCES categories(id)
            )
            """, _keepAlive))
            await create.ExecuteNonQueryAsync();

        await using (var insert = new SqliteCommand(
            """
            INSERT INTO categories(id, parent_id, name) VALUES
                (1, NULL, 'Root'),
                (2, 1, 'Child'),
                (3, 2, 'Grandchild')
            """, _keepAlive))
            await insert.ExecuteNonQueryAsync();

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task SelfReferentialGraphQlQuery_ResolvesParentAndChildrenFields()
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
                  categories(sort: [id_asc]) {
                    data {
                      id
                      name
                      categories { id name }
                      categories_children { data { id name } }
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
        var rows = doc.RootElement
            .GetProperty("data")
            .GetProperty("categories")
            .GetProperty("data")
            .EnumerateArray()
            .ToList();

        rows.Should().HaveCount(3);
        rows[0].GetProperty("categories").ValueKind.Should().Be(JsonValueKind.Null);
        rows[1].GetProperty("categories").GetProperty("name").GetString().Should().Be("Root");

        var rootChildren = rows[0].GetProperty("categories_children").GetProperty("data").EnumerateArray().ToList();
        rootChildren.Should().ContainSingle();
        rootChildren[0].GetProperty("name").GetString().Should().Be("Child");
    }
}

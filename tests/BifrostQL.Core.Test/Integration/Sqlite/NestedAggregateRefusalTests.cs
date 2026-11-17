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
/// <c>_agg</c> statements are built for root table fields only. Below the root the
/// field was parsed but no statement was emitted, and the reader threw an
/// ArgumentNullException on the missing result-set key. It is now refused with a
/// message naming the limit; the root form is the paired control.
/// </summary>
public sealed class NestedAggregateRefusalTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_nested_aggregate_refusal;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS lines",
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, label TEXT NOT NULL)",
            "CREATE TABLE lines (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES orders(id), qty INTEGER NOT NULL)",
            "INSERT INTO orders (id, label) VALUES (1, 'a')",
            "INSERT INTO lines (id, order_id, qty) VALUES (1, 1, 2), (2, 1, 3)",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> RunAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        return await new DocumentExecuter().ExecuteAsync(options =>
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
    }

    private string OrderLink => _model.Tables.Single(t => t.DbName == "lines").SingleLinks.Values.Single().ParentFieldName;

    [Fact]
    public async Task AggregateOnRootTable_IsComputed()
    {
        var result = await RunAsync("{ orders { data { id _agg(operation: Sum, value: { lines: { column: qty } }) } } }");

        result.Errors.Should().BeNullOrEmpty();
        JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement
            .GetProperty("data").GetProperty("orders").GetProperty("data")[0]
            .GetProperty("_agg").GetDouble().Should().Be(5);
    }

    [Fact]
    public async Task AggregateBelowTheRoot_IsRefusedWithAClearMessage()
    {
        var result = await RunAsync(
            $$"""{ orders { data { id lines { data { id _agg(operation: Count, value: { {{OrderLink}}: { column: id } }) } } } } }""");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain("_agg is supported only on root table fields");
    }
}

using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.VisualQuery;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Tests the build-sql / build-and-exec parsing path: a JSON VisualQuerySpec is
/// normalized (JsonElement criterion values coerced to CLR) and fed through the
/// real <see cref="VisualQueryBuilder"/> against a SQLite model — covering the
/// 2-table join acceptance case.
/// </summary>
public sealed class VisualQueryBridgeTests : IDisposable
{
    private const string SpecJson = """
    {
      "tables": [ { "table": "users" }, { "table": "orders" } ],
      "columns": [
        { "table": "users", "column": "id", "show": true, "sort": "asc", "sortOrder": 1 },
        { "table": "orders", "column": "total", "show": true, "sort": "none" }
      ],
      "joins": [
        { "leftTable": "orders", "leftColumns": ["user_id"], "rightTable": "users", "rightColumns": ["id"], "type": "inner" }
      ],
      "filter": {
        "op": "and",
        "children": [
          { "op": "leaf", "criterion": { "table": "users", "column": "id", "operator": "_in", "value": [1, 2, 3] } },
          { "op": "leaf", "criterion": { "table": "orders", "column": "total", "operator": "_between", "value": [10, 100] } },
          { "op": "leaf", "criterion": { "table": "users", "column": "name", "operator": "_eq", "value": "smith" } }
        ]
      },
      "rowLimit": 50
    }
    """;

    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public VisualQueryBridgeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-vqbridge-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static JsonElement Payload() => JsonDocument.Parse(SpecJson).RootElement;

    private async Task<IDbModel> LoadModelAsync()
    {
        var ddl = new[]
        {
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER, total REAL, " +
            "FOREIGN KEY (user_id) REFERENCES users(id))",
        };
        await using (var conn = _factory.GetConnection())
        {
            await conn.OpenAsync();
            foreach (var stmt in ddl)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        return await new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    [Fact]
    public void Parse_CoercesCriterionValuesToClr()
    {
        var spec = VisualQueryBridge.Parse(Payload());

        spec.Tables.Should().HaveCount(2);
        spec.Filter.Should().NotBeNull();
        spec.Filter!.Children.Should().HaveCount(3);

        var inCrit = spec.Filter.Children![0].Criterion!;
        inCrit.Operator.Should().Be(VisualFilterOperator.In);
        // _in value is a CLR array, not a JsonElement.
        inCrit.Value.Should().BeAssignableTo<System.Collections.IEnumerable>();
        inCrit.Value.Should().NotBeOfType<JsonElement>();
        ((System.Collections.IEnumerable)inCrit.Value!).Cast<object?>().Should().Equal(1L, 2L, 3L);

        var eqCrit = spec.Filter.Children[2].Criterion!;
        eqCrit.Value.Should().Be("smith");
        eqCrit.Value.Should().NotBeOfType<JsonElement>();
    }

    [Fact]
    public async Task ParseThenBuild_ProducesParameterizedJoinSql()
    {
        var model = await LoadModelAsync();
        var spec = VisualQueryBridge.Parse(Payload());

        var built = VisualQueryBuilder.Build(spec, model, SqliteDialect.Instance);

        built.Sql.Should().Contain("INNER JOIN");
        built.Sql.Should().Contain("IN (@p");
        built.Sql.Should().Contain("BETWEEN");
        built.Sql.Should().Contain("LIMIT 50");

        // _in(3) + _between(2) + _eq(1) = 6 params, all CLR (never JsonElement),
        // so they bind correctly as DbParameters.
        built.Parameters.Should().HaveCount(6);
        built.Parameters.Values.Should().NotContain(v => v is JsonElement);
        built.Parameters.Values.Should().Contain(new object?[] { 1L, 2L, 3L, 10L, 100L, "smith" });
    }
}

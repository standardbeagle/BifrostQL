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
/// End-to-end correctness for per-row `_agg` correlation:
///   * A ManyToOne first hop (single-link) keys the aggregate by the child FK
///     value, which differs from the row's PK. ReaderEnum must probe by that FK
///     value (not the PK) or every row matches the wrong group / null.
///   * A SUM over an all-NULL column yields SQL NULL (DBNull); it must serialize
///     as GraphQL null through the DbConvert choke point, not crash serialization.
/// </summary>
public sealed class AggregateCorrelationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_agg_correlation_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS employees");
        await Exec("DROP TABLE IF EXISTS companies");
        await Exec(
            """
            CREATE TABLE companies (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                revenue INTEGER NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE employees (
                id INTEGER PRIMARY KEY,
                company_id INTEGER NOT NULL,
                salary INTEGER NULL,
                FOREIGN KEY (company_id) REFERENCES companies(id)
            )
            """);
        // FK values (10, 20, 30) are deliberately disjoint from employee PKs
        // (1, 2, 3) so a PK-vs-FK correlation mistake surfaces as null/wrong data.
        // Each employee references a distinct company so the ManyToOne aggregate
        // group holds exactly one row (no ambiguity from shared groups).
        await Exec("INSERT INTO companies(id, name, revenue) VALUES (10, 'A', 100), (20, 'B', 200), (30, 'C', 300)");
        await Exec(
            """
            INSERT INTO employees(id, company_id, salary) VALUES
                (1, 10, NULL),
                (2, 20, NULL),
                (3, 30, 500)
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

    private async Task<JsonDocument> RunAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var executor = new DocumentExecuter();
        var factory = new SqliteDbConnFactory(ConnString);
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
    }

    [Fact]
    public async Task ManyToOneAggregate_ProbesByForeignKeyNotPrimaryKey()
    {
        // Following the single-link employees -> companies and summing company
        // revenue per employee. The correlation key is company_id (FK), which is
        // disjoint from the employee PK; probing by PK would return null.
        using var doc = await RunAsync(
            """
            {
              employees(sort: [id_asc]) {
                data { id total: _agg(value: { companies: { column: revenue } } operation: Sum) }
              }
            }
            """);

        var rows = doc.RootElement.GetProperty("data").GetProperty("employees")
            .GetProperty("data").EnumerateArray().ToList();

        rows.Should().HaveCount(3);
        // emp1 -> company 10 (revenue 100); emp2 -> company 20 (200); emp3 -> 30 (300).
        double.Parse(rows[0].GetProperty("total").ToString()).Should().Be(100);
        double.Parse(rows[1].GetProperty("total").ToString()).Should().Be(200);
        double.Parse(rows[2].GetProperty("total").ToString()).Should().Be(300);
    }

    [Fact]
    public async Task AllNullSumAggregate_SerializesAsNull_NotDbNull()
    {
        // Following the multi-link companies -> employees and summing salary.
        // Company A's employees all have NULL salary, so SUM is SQL NULL (DBNull);
        // it must serialize as GraphQL null instead of breaking scalar serialization.
        using var doc = await RunAsync(
            """
            {
              companies(sort: [id_asc]) {
                data { id total: _agg(value: { employees: { column: salary } } operation: Sum) }
              }
            }
            """);

        var rows = doc.RootElement.GetProperty("data").GetProperty("companies")
            .GetProperty("data").EnumerateArray().ToList();

        rows.Should().HaveCount(3);
        // Companies A and B: employee salary is NULL -> SUM is null (not a crash).
        rows[0].GetProperty("total").ValueKind.Should().Be(JsonValueKind.Null);
        rows[1].GetProperty("total").ValueKind.Should().Be(JsonValueKind.Null);
        // Company C: single employee with salary 500.
        double.Parse(rows[2].GetProperty("total").ToString()).Should().Be(500);
    }
}

using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Live-SQLite proof that a scope filter (the shape a tenant/soft-delete
/// transformer produces) actually excludes rows from BOTH pivot paths: the
/// distinct-value probe that builds the column headers AND the pivot cell
/// aggregates. The mutation check runs each statement WITHOUT the filter and
/// asserts the out-of-scope row reappears — so the tests fail if the filter is
/// ever dropped from either path.
/// </summary>
public sealed class PivotSecurityFilterTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_pivot_security_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private readonly SqliteDbConnFactory _factory = new(ConnString);
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                region TEXT NOT NULL,
                status TEXT NOT NULL,
                amount REAL NOT NULL
            )
            """);
        // Tenant 1: east/open 100, east/closed 40, west/open 25.
        // Tenant 2: east/shipped 999 — a status value that must NOT surface as a
        // pivot column for a tenant-1 caller, nor contribute to any cell.
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, region, status, amount) VALUES
                (1, 1, 'east', 'open',    100),
                (2, 1, 'east', 'closed',  40),
                (3, 1, 'west', 'open',    25),
                (4, 2, 'east', 'shipped', 999)
            """);
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static PivotQueryConfig Config() =>
        PivotQueryConfig.Create("status", "amount", "Sum", new[] { "region" });

    private static ParameterizedSql TenantOneFilter() =>
        new($" WHERE {Dialect.EscapeIdentifier("tenant_id")} = @p0",
            new[] { new SqlParameterInfo("@p0", 1) });

    private async Task<List<object?[]>> RunAsync(ParameterizedSql sql)
    {
        var parameters = sql.Parameters.ToDictionary(p => p.Name, p => p.Value);
        var result = await RawSqlExecutor.ExecuteAsync(_factory, sql.Sql, parameters, 30, 1000);
        return result.Rows.ToList();
    }

    [Fact]
    public async Task DistinctValues_WithTenantFilter_ExcludesOtherTenantColumns()
    {
        var tableRef = Dialect.TableReference(null, "orders");

        var filtered = await RunAsync(
            PivotSqlGenerator.GenerateDistinctValuesSql(Dialect, "status", tableRef, TenantOneFilter()));
        var statuses = filtered.Select(r => (string?)r[0]).ToList();
        statuses.Should().BeEquivalentTo(new[] { "closed", "open" });
        statuses.Should().NotContain("shipped", "tenant 2's status must not become a pivot column");

        // Mutation check: drop the filter and tenant 2's 'shipped' reappears.
        var unfiltered = await RunAsync(
            PivotSqlGenerator.GenerateDistinctValuesSql(Dialect, "status", tableRef));
        unfiltered.Select(r => (string?)r[0]).Should().Contain("shipped");
    }

    [Fact]
    public async Task Pivot_WithTenantFilter_ExcludesOtherTenantFromCells()
    {
        var tableRef = Dialect.TableReference(null, "orders");
        object?[] pivotValues = { "open", "closed", "shipped" };

        var filtered = await RunAsync(
            PivotSqlGenerator.GeneratePivot(Dialect, Config(), tableRef, pivotValues, TenantOneFilter()));

        // Columns: region, open, closed, shipped. east row: open=100, closed=40,
        // shipped=NULL (tenant 2's shipped/999 is filtered out).
        var east = filtered.Single(r => (string?)r[0] == "east");
        Convert.ToDouble(east[1]).Should().Be(100);   // open
        Convert.ToDouble(east[2]).Should().Be(40);     // closed
        east[3].Should().Match(v => v == null || v is System.DBNull, "tenant 2's shipped/999 must not land in any cell");

        // Mutation check: without the filter, shipped=999 appears for east.
        var unfiltered = await RunAsync(
            PivotSqlGenerator.GeneratePivot(Dialect, Config(), tableRef, pivotValues));
        var eastAll = unfiltered.Single(r => (string?)r[0] == "east");
        Convert.ToDouble(eastAll[3]).Should().Be(999);
    }
}

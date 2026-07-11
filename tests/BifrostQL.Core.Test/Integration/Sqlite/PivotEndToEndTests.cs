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
/// End-to-end coverage for the per-table PIVOT root field
/// (<c>&lt;table&gt;Pivot(rowKeys, pivotColumn, valueColumn, aggregate, filter): JSON!</c>):
/// schema emission, resolver wiring, the SQLite CASE-WHEN dialect path, the JSON
/// payload shape, the cardinality guard, and — the security contract — that the
/// tenant filter excludes other tenants' rows from BOTH the discovered pivot
/// columns AND the aggregated cells. A tenant-1 caller must never see tenant 2's
/// <c>shipped</c> status as a column header or in a cell.
/// </summary>
public sealed class PivotEndToEndTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_pivot_e2e_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "*.orders { tenant-filter: tenant_id }",
        "*.events { tenant-filter: tenant_id }",
        "*.tags { tenant-filter: tenant_id }",
    };

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
        // Tenant 2: east/shipped 999 — 'shipped' must never surface as a pivot
        // column for a tenant-1 caller, nor land in any cell.
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, region, status, amount) VALUES
                (1, 1, 'east', 'open',    100),
                (2, 1, 'east', 'closed',  40),
                (3, 1, 'west', 'open',    25),
                (4, 2, 'east', 'shipped', 999)
            """);

        // events: 101 distinct 'kind' values for tenant 1 — one over the pivot
        // cardinality cap (100), to exercise the guard.
        await Exec("DROP TABLE IF EXISTS events");
        await Exec(
            """
            CREATE TABLE events (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                grp TEXT NOT NULL,
                kind TEXT NOT NULL,
                val REAL NOT NULL
            )
            """);
        for (var i = 1; i <= 101; i++)
            await Exec($"INSERT INTO events(id, tenant_id, grp, kind, val) VALUES ({i}, 1, 'x', 'k{i:000}', 1)");

        // tags: a NULL label alongside a literal '_null_' — both map to the null
        // label, so the pivot would emit two identically-named output columns.
        await Exec("DROP TABLE IF EXISTS tags");
        await Exec(
            """
            CREATE TABLE tags (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                grp TEXT NOT NULL,
                label TEXT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO tags(id, tenant_id, grp, label) VALUES
                (1, 1, 'x', NULL),
                (2, 1, 'x', '_null_')
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Rules));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> ExecuteAsTenantAsync(string query, int tenantId)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new TenantFilterTransformer() },
        });
        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?> { ["tenant_id"] = tenantId };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    private async Task<JsonElement> PivotAsTenantAsync(string query, int tenantId)
    {
        var execution = await ExecuteAsTenantAsync(query, tenantId);
        execution.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        return doc.RootElement.GetProperty("data").GetProperty("ordersPivot").Clone();
    }

    private static Dictionary<string, JsonElement> RowsByKey(JsonElement pivot, string rowKey)
        => pivot.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty(rowKey).GetString()!, r => r);

    [Fact]
    public async Task Pivot_CrossTabsWithinTenantScope_ShapeAndValues()
    {
        var pivot = await PivotAsTenantAsync(
            """
            { ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum) }
            """, tenantId: 1);

        pivot.GetProperty("pivotColumn").GetString().Should().Be("status");
        pivot.GetProperty("rowKeys").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("region");

        // Columns are the tenant-1 distinct statuses, ordered — never 'shipped'.
        pivot.GetProperty("columns").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("closed", "open");

        var byRegion = RowsByKey(pivot, "region");
        byRegion.Should().HaveCount(2);

        var east = byRegion["east"].GetProperty("cells");
        east.GetProperty("open").GetDouble().Should().Be(100);
        east.GetProperty("closed").GetDouble().Should().Be(40);

        // west has an 'open' order but no 'closed' one — that cell is null, not absent.
        var west = byRegion["west"].GetProperty("cells");
        west.GetProperty("open").GetDouble().Should().Be(25);
        west.GetProperty("closed").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Pivot_ExcludesOtherTenantFromColumnsAndCells()
    {
        var tenant1 = await PivotAsTenantAsync(
            """
            { ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum) }
            """, tenantId: 1);

        tenant1.GetProperty("columns").EnumerateArray().Select(e => e.GetString())
            .Should().NotContain("shipped", "tenant 2's status must not become a pivot column");

        // The tenant-2 caller sees only its own 'shipped' status and row.
        var tenant2 = await PivotAsTenantAsync(
            """
            { ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum) }
            """, tenantId: 2);

        tenant2.GetProperty("columns").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("shipped");
        var east = RowsByKey(tenant2, "region")["east"].GetProperty("cells");
        east.GetProperty("shipped").GetDouble().Should().Be(999);
    }

    [Fact]
    public async Task Pivot_CountAggregate_TabulatesRowCounts()
    {
        var pivot = await PivotAsTenantAsync(
            """
            { ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: id, aggregate: count) }
            """, tenantId: 1);

        var east = RowsByKey(pivot, "region")["east"].GetProperty("cells");
        east.GetProperty("open").GetDouble().Should().Be(1);
        east.GetProperty("closed").GetDouble().Should().Be(1);
    }

    [Fact]
    public async Task Pivot_CardinalityAboveCap_ErrorsWithSteering()
    {
        // events has 101 distinct 'kind' values in tenant-1 scope, one over the cap.
        var execution = await ExecuteAsTenantAsync(
            """
            { eventsPivot(rowKeys: [grp], pivotColumn: kind, valueColumn: val, aggregate: count) }
            """, tenantId: 1);

        execution.Errors.Should().NotBeNullOrEmpty();
        (execution.Errors![0].InnerException?.Message ?? execution.Errors![0].Message)
            .Should().Contain("exceeding the limit").And.Contain("101");
    }

    [Fact]
    public async Task Pivot_PivotColumnRepeatedInRowKeys_Errors()
    {
        // The pivot column cannot also be a row key — PivotQueryConfig.Create rejects
        // it, and the resolver surfaces that as a clean GraphQL error.
        var execution = await ExecuteAsTenantAsync(
            """
            { ordersPivot(rowKeys: [status], pivotColumn: status, valueColumn: amount, aggregate: sum) }
            """, tenantId: 1);

        execution.Errors.Should().NotBeNullOrEmpty();
        (execution.Errors![0].InnerException?.Message ?? execution.Errors![0].Message)
            .Should().Contain("must not appear in group-by");
    }

    [Fact]
    public async Task Pivot_DuplicateOutputColumnLabel_ErrorsCleanly()
    {
        // A NULL value and a literal '_null_' both take the null label, so the pivot
        // would produce two output columns named '_null_'. That is unrepresentable in
        // the result set and the JSON payload alike — it must fail with an actionable
        // message, not the reader's opaque duplicate-column-name failure.
        var execution = await ExecuteAsTenantAsync(
            """
            { tagsPivot(rowKeys: [grp], pivotColumn: label, valueColumn: id, aggregate: count) }
            """, tenantId: 1);

        execution.Errors.Should().NotBeNullOrEmpty();
        (execution.Errors![0].InnerException?.Message ?? execution.Errors![0].Message)
            .Should().Contain("two output columns named '_null_'");
    }
}

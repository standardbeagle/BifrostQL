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
/// End-to-end coverage for the dedicated per-table GROUP BY aggregate root field
/// (<c>&lt;table&gt;Aggregate(groupBy) { ... }</c>): schema emission, resolver
/// wiring, dialect SQL, and result reading against seeded SQLite. Proves the
/// aggregate values (count/sum/avg/min/max, multi-column groupBy, empty result)
/// AND — the security contract — that rows excluded by the tenant filter never
/// reach the aggregate, so a tenant-1 caller's totals exclude tenant 2's rows.
/// </summary>
public sealed class GroupedAggregateEndToEndTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_grouped_aggregate_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "*.orders { tenant-filter: tenant_id }",
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
                amount REAL NOT NULL
            )
            """);
        // Tenant 1: east {100, 200}, west {50}. Tenant 2: east {999} — must be
        // invisible to a tenant-1 caller, so its 999 never lands in east's sum.
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, region, amount) VALUES
                (1, 1, 'east', 100),
                (2, 1, 'east', 200),
                (3, 1, 'west', 50),
                (4, 2, 'east', 999)
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

    private async Task<JsonDocument> RunAsTenantAsync(string query, int tenantId)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new TenantFilterTransformer() },
        });
        var executor = new DocumentExecuter();
        var execution = await executor.ExecuteAsync(options =>
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

        execution.Errors.Should().BeNullOrEmpty();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
    }

    private static Dictionary<string, JsonElement> ByRegion(JsonDocument doc)
        => doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray()
            .ToDictionary(r => r.GetProperty("region").GetString()!, r => r);

    [Fact]
    public async Task GroupedAggregate_ComputesCountSumAvgMinMax_PerGroup()
    {
        using var doc = await RunAsTenantAsync(
            """
            {
              ordersAggregate(groupBy: [region]) {
                region
                _count
                _sum { amount }
                _avg { amount }
                _min { amount }
                _max { amount }
              }
            }
            """, tenantId: 1);

        var groups = ByRegion(doc);
        groups.Should().HaveCount(2);

        var east = groups["east"];
        east.GetProperty("_count").GetInt32().Should().Be(2);
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
        east.GetProperty("_avg").GetProperty("amount").GetDouble().Should().Be(150);
        east.GetProperty("_min").GetProperty("amount").GetDouble().Should().Be(100);
        east.GetProperty("_max").GetProperty("amount").GetDouble().Should().Be(200);

        var west = groups["west"];
        west.GetProperty("_count").GetInt32().Should().Be(1);
        west.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(50);
    }

    [Fact]
    public async Task GroupedAggregate_ExcludesRowsOutsideTenantScope()
    {
        using var doc = await RunAsTenantAsync(
            """
            { ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }
            """, tenantId: 1);

        // Tenant 2's east row (amount 999) must not contribute: east sum stays 300.
        var east = ByRegion(doc)["east"];
        east.GetProperty("_count").GetInt32().Should().Be(2);
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);

        // The tenant-2 caller sees only its own row.
        using var doc2 = await RunAsTenantAsync(
            """
            { ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }
            """, tenantId: 2);
        var groups2 = ByRegion(doc2);
        groups2.Should().ContainKey("east").And.NotContainKey("west");
        groups2["east"].GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(999);
    }

    [Fact]
    public async Task GroupedAggregate_EmptyResult_ReturnsNoGroups()
    {
        using var doc = await RunAsTenantAsync(
            """
            { ordersAggregate(filter: { region: { _eq: "nowhere" } }, groupBy: [region]) { region _count } }
            """, tenantId: 1);

        doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GroupedAggregate_NoGroupBy_ReturnsSingleWholeTableRow()
    {
        using var doc = await RunAsTenantAsync(
            """
            { ordersAggregate { _count _sum { amount } } }
            """, tenantId: 1);

        var rows = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetProperty("_count").GetInt32().Should().Be(3);
        rows[0].GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(350);
    }
}

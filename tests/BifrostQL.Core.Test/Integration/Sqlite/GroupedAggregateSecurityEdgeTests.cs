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
/// Security-transformer and edge-case coverage for the <c>&lt;table&gt;Aggregate</c>
/// GROUP BY surface: soft-delete scoping (deleted rows excluded from group
/// aggregates), NULL group keys, and a whole-table aggregate over zero surviving
/// rows (COUNT 0, SUM NULL rather than a crash or a missing row).
/// </summary>
public sealed class GroupedAggregateSecurityEdgeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_grouped_aggregate_edge_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at }",
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
                region TEXT NULL,
                amount REAL NOT NULL,
                deleted_at TEXT NULL
            )
            """);
        // Tenant 1: east live 100; east soft-deleted 200 (excluded); west live 50;
        // NULL-region live 70. Tenant 2 east 999 (excluded by tenant filter).
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, region, amount, deleted_at) VALUES
                (1, 1, 'east', 100, NULL),
                (2, 1, 'east', 200, '2020-01-01T00:00:00'),
                (3, 1, 'west', 50,  NULL),
                (4, 1, NULL,   70,  NULL),
                (5, 2, 'east', 999, NULL)
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

    private async Task<JsonDocument> RunAsTenantOneAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer(),
            },
        });
        var executor = new DocumentExecuter();
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?> { ["tenant_id"] = 1 };
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

    private static List<JsonElement> Groups(JsonDocument doc) =>
        doc.RootElement.GetProperty("data").GetProperty("ordersAggregate").EnumerateArray().ToList();

    [Fact]
    public async Task GroupedAggregate_ExcludesSoftDeletedRows()
    {
        using var doc = await RunAsTenantOneAsync(
            "{ ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }");

        var east = Groups(doc).Single(g =>
            g.GetProperty("region").ValueKind == JsonValueKind.String &&
            g.GetProperty("region").GetString() == "east");

        // The soft-deleted east row (200) is excluded: only the live 100 counts.
        east.GetProperty("_count").GetInt32().Should().Be(1);
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(100);
    }

    [Fact]
    public async Task GroupedAggregate_NullGroupKey_FormsItsOwnGroup()
    {
        using var doc = await RunAsTenantOneAsync(
            "{ ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }");

        var nullGroup = Groups(doc).Single(g => g.GetProperty("region").ValueKind == JsonValueKind.Null);
        nullGroup.GetProperty("_count").GetInt32().Should().Be(1);
        nullGroup.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(70);
    }

    [Fact]
    public async Task GroupedAggregate_WholeTableOverZeroRows_ReturnsCountZeroSumNull()
    {
        using var doc = await RunAsTenantOneAsync(
            """
            { ordersAggregate(filter: { region: { _eq: "nowhere" } }) { _count _sum { amount } } }
            """);

        var rows = Groups(doc);
        rows.Should().HaveCount(1, "a whole-table aggregate always yields one row, even over zero rows");
        rows[0].GetProperty("_count").GetInt32().Should().Be(0);
        rows[0].GetProperty("_sum").GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

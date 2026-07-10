using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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

/// <summary>
/// Security-gate proof (5.1) for the wired <c>&lt;table&gt;Aggregate</c> path: it
/// resolves its filter transformers through the SAME profile-filtered service the
/// row resolvers use, so a client-selectable profile that lists only an
/// application module — and omits the security band — still cannot strip the
/// tenant filter. A cross-tenant row must stay excluded from the aggregate under
/// that profile, proving the path is not reaching a raw, unfiltered transformer set.
/// </summary>
public sealed class GroupedAggregateProfileGatingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_grouped_aggregate_profile_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "*.orders { tenant-filter: tenant_id }",
    };

    /// <summary>Application-band (priority 200) transformer a profile may toggle; a no-op filter.</summary>
    private sealed class AppReportFilterTransformer : IFilterTransformer, IModuleNamed
    {
        public int Priority => 200;
        public string ModuleName => "app-report";
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

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
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, region, amount) VALUES
                (1, 1, 'east', 100),
                (2, 1, 'east', 200),
                (3, 2, 'east', 999)
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task AggregatePath_UnderProfileOmittingSecurity_StillExcludesOtherTenant()
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        // A named profile that enables ONLY the application module. FilterBy must keep
        // the security-band tenant filter (priority 0) regardless of the profile's list.
        var profile = new BifrostProfile { Name = "reporting", Modules = new[] { "app-report" } };
        var allTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),      // security band (priority 0)
                new AppReportFilterTransformer(),   // application band (priority 200)
            },
        };
        var profileFiltered = BifrostProfileRegistry.FilterBy(allTransformers, profile);
        var transformerService = new QueryTransformerService(profileFiltered);

        var executor = new DocumentExecuter();
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = "{ ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }";
            options.UserContext = new Dictionary<string, object?> { ["tenant_id"] = 1 };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));

        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");

        // Tenant 2's row (amount 999) must be excluded despite the profile omitting the
        // security module: count 2, sum 300 — not 3 / 1299.
        east.GetProperty("_count").GetInt32().Should().Be(2);
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
    }
}

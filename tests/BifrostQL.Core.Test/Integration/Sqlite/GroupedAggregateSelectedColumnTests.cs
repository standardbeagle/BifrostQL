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
/// Finding M8: the <c>&lt;table&gt;Aggregate</c> resolver built a value column for
/// EVERY numeric column of the table whenever any op group (<c>_sum</c>/…) was
/// selected, and <c>QueryTransformerService</c> feeds all value columns to the
/// column read guard. A query aggregating only an allowed column was therefore
/// denied whenever the table had ANY policy-read-denied numeric column, and the
/// generated SQL aggregated columns the client never asked for.
///
/// The sibling guard tests (<see cref="QueryTransformerServiceReadGuardTests"/>)
/// build <c>GroupedAggregate</c> by hand, so they cannot manifest this bug — the
/// bug is in the resolver's derivation of value columns from the selection set,
/// which only exists on the wired GraphQL path.
/// </summary>
public sealed class GroupedAggregateSelectedColumnTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_grouped_aggregate_selected_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "main.orders { policy-actions: read; policy-read-deny: salary }",
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
                region TEXT NOT NULL,
                amount REAL NOT NULL,
                salary INTEGER NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO orders(id, region, amount, salary) VALUES
                (1, 'east', 100, 250000),
                (2, 'east', 200, 260000)
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> AggregateAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(),
            },
        });

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "test-user",
                ["roles"] = new[] { "bifrost-admin" },
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    private const string PolicyReadDeniedMessage =
        "The query references a field that is not permitted by authorization policy.";

    [Fact]
    public async Task GroupedAggregate_SumOfAllowedColumn_DeniedSiblingNotAggregated_Succeeds()
    {
        // salary is policy-read-denied for this caller, but the query never selects
        // it. The resolver must not load (and the guard must not see) a column the
        // client did not ask to aggregate.
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }");

        result.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
    }

    [Fact]
    public async Task GroupedAggregate_SumOfDeniedColumn_IsRejected()
    {
        // Over-correction fence: deriving value columns from the selection must not
        // weaken the guard for a denied column the client DID select.
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _sum { salary } } }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.Message.Should().Be(PolicyReadDeniedMessage);
        new GraphQLSerializer().Serialize(result).Should().NotContain("250000");
    }
}

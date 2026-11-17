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
/// A field the client excluded with <c>@skip</c>/<c>@include</c> is not part of the
/// request. Neither the table query tree (<c>SqlVisitor</c>) nor the selection walk
/// the aggregate resolver uses (<c>SelectionWalk</c>) may project it, so the column
/// read guard never judges a policy-denied column the caller did not ask for. Each
/// excluded shape (field, inline fragment, fragment spread; literal and variable
/// condition) has a paired included control that the guard refuses, which proves the
/// directive, not the fixture, is what lets the query through.
/// </summary>
public sealed class SkipIncludeSelectionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_skip_include_selection;Mode=Memory;Cache=Shared";
    private const string PolicyReadDeniedMessage =
        "The query references a field that is not permitted by authorization policy.";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount REAL NOT NULL, salary INTEGER NOT NULL)",
            "INSERT INTO orders (id, region, amount, salary) VALUES (1, 'east', 100, 250000), (2, 'east', 200, 260000)",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString),
            new MetadataLoader(new[] { "main.orders { policy-actions: read; policy-read-deny: salary }" })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> RunAsync(string query, bool show)
    {
        var schema = DbSchema.FromModel(_model);
        var transformers = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        });
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.Variables = new Inputs(new Dictionary<string, object?> { ["show"] = show });
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "test-user",
                ["roles"] = new[] { "member" },
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformers),
            });
        });
    }

    public static TheoryData<string, string> TableShapes => new()
    {
        { "field, variable", "query ($show: Boolean!) { orders { data { id salary @include(if: $show) } } }" },
        { "field, literal skip", "query ($show: Boolean!) { orders { data { id salary @skip(if: true) @include(if: $show) } } }" },
        { "inline fragment", "query ($show: Boolean!) { orders { data { id ... @include(if: $show) { salary } } } }" },
        { "fragment spread", "query ($show: Boolean!) { orders { data { id ...Pay @include(if: $show) } } } fragment Pay on orders { salary }" },
    };

    [Theory]
    [MemberData(nameof(TableShapes))]
    public async Task TableQuery_ExcludedDeniedColumn_IsNotProjected(string shape, string query)
    {
        var result = await RunAsync(query, show: false);

        result.Errors.Should().BeNullOrEmpty(shape);
        var json = new GraphQLSerializer().Serialize(result);
        var rows = JsonDocument.Parse(json).RootElement.GetProperty("data").GetProperty("orders").GetProperty("data");
        rows.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).Should().Equal(1, 2);
        rows.EnumerateArray().Select(r => r.TryGetProperty("salary", out var _)).Should().AllBeEquivalentTo(false);
    }

    [Theory]
    [MemberData(nameof(TableShapes))]
    public async Task TableQuery_IncludedDeniedColumn_IsRefused(string shape, string query)
    {
        if (shape == "field, literal skip")
            return; // @skip(if: true) wins over any @include: there is no included form.

        var result = await RunAsync(query, show: true);

        result.Errors.Should().NotBeNullOrEmpty(shape);
        result.Errors![0].InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.Message.Should().Be(PolicyReadDeniedMessage);
    }

    private const string AggregateQuery =
        "query ($show: Boolean!) { ordersAggregate(groupBy: [region]) { region _sum { amount salary @include(if: $show) } } }";

    [Fact]
    public async Task Aggregate_ExcludedDeniedColumn_IsNotAggregated()
    {
        var result = await RunAsync(AggregateQuery, show: false);

        result.Errors.Should().BeNullOrEmpty();
        var east = JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement
            .GetProperty("data").GetProperty("ordersAggregate").EnumerateArray().Single();
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
        east.GetProperty("_sum").TryGetProperty("salary", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Aggregate_IncludedDeniedColumn_IsRefused()
    {
        var result = await RunAsync(AggregateQuery, show: true);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.Message.Should().Be(PolicyReadDeniedMessage);
    }
}

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
/// Finding H7, end to end: <c>groupBy: [id]</c> is one group per row, so an
/// unbounded grouped aggregate was a wire-reachable whole-table read that the
/// model's <c>max-query-rows</c> ceiling did not see. The grouped surface now
/// pages like every other read — bounded by the ceiling, with <c>limit</c>/
/// <c>offset</c> arguments that can only NARROW the server's window.
/// </summary>
public sealed class GroupedAggregateGroupCapTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_grouped_aggregate_cap_test;Mode=Memory;Cache=Shared";
    private const int Ceiling = 5;
    private const int RowCount = 40;

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        $":root {{ {MetadataKeys.Model.MaxQueryRows}: {Ceiling} }}",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS events");
        await Exec(
            """
            CREATE TABLE events (
                id INTEGER PRIMARY KEY,
                amount REAL NOT NULL
            )
            """);
        for (var i = 1; i <= RowCount; i++)
            await Exec($"INSERT INTO events(id, amount) VALUES ({i}, {i})");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Rules));
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
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
    }

    private static List<int> Ids(JsonDocument doc)
        => doc.RootElement.GetProperty("data").GetProperty("eventsAggregate")
            .EnumerateArray().Select(g => g.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task GroupByPrimaryKey_ReturnsCeilingGroups_NotEveryRow()
    {
        using var doc = await RunAsync("{ eventsAggregate(groupBy: [id]) { id _count } }");

        Ids(doc).Should().HaveCount(Ceiling,
            "groupBy:[id] is one group per row; unbounded it reads the whole table past max-query-rows");
    }

    [Fact]
    public async Task ClientLimit_MayNarrow_ButNeverRaisesTheCeiling()
    {
        using var narrowed = await RunAsync("{ eventsAggregate(groupBy: [id], limit: 2) { id _count } }");
        Ids(narrowed).Should().HaveCount(2);

        // Both the explicit over-ceiling limit and the no-limit sentinel clamp
        // back to the server ceiling — fail closed, never a wider read.
        using var raised = await RunAsync($"{{ eventsAggregate(groupBy: [id], limit: {RowCount}) {{ id _count }} }}");
        Ids(raised).Should().HaveCount(Ceiling);

        using var sentinel = await RunAsync("{ eventsAggregate(groupBy: [id], limit: -1) { id _count } }");
        Ids(sentinel).Should().HaveCount(Ceiling);
    }

    [Fact]
    public async Task OffsetPagesDeterministicallyByGroupKey()
    {
        using var first = await RunAsync("{ eventsAggregate(groupBy: [id], limit: 3) { id _count } }");
        using var second = await RunAsync("{ eventsAggregate(groupBy: [id], limit: 3, offset: 3) { id _count } }");

        // Group-key ordering makes the two windows disjoint and contiguous; an
        // unordered LIMIT could return any qualifying groups on either page.
        Ids(first).Should().Equal(1, 2, 3);
        Ids(second).Should().Equal(4, 5, 6);
    }
}

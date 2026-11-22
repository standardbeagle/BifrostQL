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
/// The <c>&lt;table&gt;Pivot</c> cross-tab is a GROUP BY over the row keys, so
/// <c>rowKeys: [id]</c> is one output row per table row. It was never paged: only the
/// distinct pivot-value discovery was capped, and the cross-tab returned every row past
/// <c>max-query-rows</c>. It now takes the ceiling as its row window (ordered by the row
/// keys) and reports a cut with a cap+1 sentinel in its own payload's <c>truncated</c> key.
/// </summary>
public sealed class PivotRowCeilingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_pivot_row_ceiling_test;Mode=Memory;Cache=Shared";
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

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                status TEXT NOT NULL,
                amount REAL NOT NULL
            )
            """);
        // Inserted in descending id order so an unordered cap would not happen to
        // return the lowest ids.
        for (var i = RowCount; i >= 1; i--)
            await Exec($"INSERT INTO orders(id, status, amount) VALUES ({i}, '{(i % 2 == 0 ? "open" : "closed")}', {i})");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Rules));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<JsonElement> PivotAsync(string query)
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
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        return doc.RootElement.GetProperty("data").GetProperty("ordersPivot").Clone();
    }

    private static List<int> RowIds(JsonElement pivot)
        => pivot.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task PivotByPrimaryKey_ReturnsCeilingRowsInKeyOrder_AndReportsTruncation()
    {
        var pivot = await PivotAsync(
            "{ ordersPivot(rowKeys: [id], pivotColumn: status, valueColumn: amount, aggregate: sum) }");

        RowIds(pivot).Should().Equal(new[] { 1, 2, 3, 4, 5 },
            "rowKeys:[id] is one row per order; the cross-tab must stop at max-query-rows, ordered by the row keys");
        pivot.GetProperty("truncated").GetBoolean().Should().BeTrue(
            "the cap+1 sentinel row says more rows exist, so the payload must report the cut");
    }

    [Fact]
    public async Task PivotOfExactlyCeilingRows_IsNotTruncated()
    {
        var pivot = await PivotAsync(
            $"{{ ordersPivot(rowKeys: [id], pivotColumn: status, valueColumn: amount, aggregate: sum, filter: {{ id: {{ _lte: {Ceiling} }} }}) }}");

        RowIds(pivot).Should().Equal(1, 2, 3, 4, 5);
        pivot.GetProperty("truncated").GetBoolean().Should().BeFalse(
            "exactly a ceiling of rows returns no sentinel row");
    }
}

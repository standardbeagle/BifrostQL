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
/// The per-row <c>_agg</c> field writes each result in the form the grouped
/// <c>&lt;table&gt;Aggregate</c> surface gives the same aggregate
/// (<see cref="AggregateSurface.RowAggregateScalar"/>): COUNT as a JSON integer, an
/// exact (BigInt/Decimal) column's SUM/MIN/MAX/AVG as a decimal string, any other
/// numeric column's as a JSON number. Typed <c>Float</c>, a bigint SUM past 2^53
/// reached the client rounded.
/// </summary>
public sealed class AggregateRowResultFormTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_agg_row_result_form_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS ledger_lines");
        await Exec("DROP TABLE IF EXISTS ledgers");
        await Exec("CREATE TABLE ledgers (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE ledger_lines (
                id INTEGER PRIMARY KEY,
                ledger_id INTEGER NOT NULL,
                amount BIGINT NOT NULL,
                ratio REAL NOT NULL,
                FOREIGN KEY (ledger_id) REFERENCES ledgers(id)
            )
            """);
        await Exec("INSERT INTO ledgers(id, name) VALUES (1, 'wide')");
        // 2^53 + 1 is the first integer a double cannot hold; the sum
        // 9007199254740995 rounds to ...996 through a double.
        await Exec(
            """
            INSERT INTO ledger_lines(id, ledger_id, amount, ratio) VALUES
                (1, 1, 9007199254740993, 2.5),
                (2, 1, 2, 5.0)
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

    private async Task<JsonElement> RunLedgerRowAsync(string aggSelection)
    {
        var schema = DbSchema.FromModel(_model);
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ ledgers {{ data {{ id {aggSelection} }} }} }}";
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        return doc.RootElement.GetProperty("data").GetProperty("ledgers").GetProperty("data")[0].Clone();
    }

    [Fact]
    public async Task Count_IsJsonInteger()
    {
        var row = await RunLedgerRowAsync("n: _agg(value: { ledger_lines: { column: id } } operation: Count)");

        row.GetProperty("n").ValueKind.Should().Be(JsonValueKind.Number);
        row.GetProperty("n").GetRawText().Should().Be("2");
    }

    [Fact]
    public async Task BigIntSum_IsExactDecimalString()
    {
        var row = await RunLedgerRowAsync("total: _agg(value: { ledger_lines: { column: amount } } operation: Sum)");

        row.GetProperty("total").ValueKind.Should().Be(JsonValueKind.String);
        row.GetProperty("total").GetString().Should().Be("9007199254740995");
    }

    [Fact]
    public async Task BigIntMax_IsExactDecimalString()
    {
        var row = await RunLedgerRowAsync("top: _agg(value: { ledger_lines: { column: amount } } operation: Max)");

        row.GetProperty("top").ValueKind.Should().Be(JsonValueKind.String);
        row.GetProperty("top").GetString().Should().Be("9007199254740993");
    }

    /// <summary>Control: a Float column keeps a JSON number.</summary>
    [Fact]
    public async Task FloatSum_IsJsonNumber()
    {
        var row = await RunLedgerRowAsync("total: _agg(value: { ledger_lines: { column: ratio } } operation: Sum)");

        row.GetProperty("total").ValueKind.Should().Be(JsonValueKind.Number);
        row.GetProperty("total").GetDouble().Should().Be(7.5);
    }

    /// <summary>
    /// The grouped surface shares the rule (<see cref="AggregateSurface.ValueScalar"/>):
    /// a bigint column's <c>_sum</c> is Decimal; the Float column beside it is the control.
    /// </summary>
    [Fact]
    public async Task GroupedBigIntSum_IsExactDecimalString()
    {
        var schema = DbSchema.FromModel(_model);
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = "{ ledger_linesAggregate(groupBy: [ledger_id]) { ledger_id _sum { amount ratio } } }";
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        var sum = doc.RootElement.GetProperty("data").GetProperty("ledger_linesAggregate")[0].GetProperty("_sum");
        sum.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.String);
        sum.GetProperty("amount").GetString().Should().Be("9007199254740995");
        sum.GetProperty("ratio").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Schema_TypesAggAsAggregateNumber()
    {
        var schema = DbSchema.FromModel(_model);
        schema.Initialize();
        var field = schema.AllTypes["ledgers"].Should().BeAssignableTo<GraphQL.Types.IComplexGraphType>()
            .Subject.GetField("_agg");

        field!.ResolvedType!.GetNamedType().Name.Should().Be(AggregateNumberGraphType.TypeName);
    }
}

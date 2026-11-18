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
        await Exec("DROP TABLE IF EXISTS quote_lines");
        await Exec("DROP TABLE IF EXISTS quotes");
        await Exec("CREATE TABLE quotes (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE quote_lines (
                id INTEGER PRIMARY KEY,
                quote_id INTEGER NOT NULL,
                price DECIMAL(10,2) NULL,
                FOREIGN KEY (quote_id) REFERENCES quotes(id)
            )
            """);
        // Quote 1: SQLite keeps 'N/A' as text in a DECIMAL column, and MAX ranks text above
        // every number. Quote 2: 1e300 is stored as REAL, past decimal's range.
        await Exec("INSERT INTO quotes(id, name) VALUES (1, 'text'), (2, 'huge'), (3, 'plain')");
        await Exec(
            """
            INSERT INTO quote_lines(id, quote_id, price) VALUES
                (1, 1, 'N/A'), (2, 1, 5),
                (3, 2, 1e300),
                (4, 3, 12.5)
            """);
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

    private async Task<ExecutionResult> RunQuoteAsync(int quoteId, string aggSelection)
    {
        var schema = DbSchema.FromModel(_model);
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ quotes(filter: {{ id: {{ _eq: {quoteId} }} }}) {{ data {{ id {aggSelection} }} }} }}";
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });
    }

    /// <summary>
    /// A Decimal aggregate no decimal holds is a field error that names no cell data, never
    /// a raw parse or overflow exception: <c>decimal.Parse("N/A")</c> threw a FormatException
    /// whose text carries the cell, and <c>Convert.ToDecimal(1e300)</c> an OverflowException.
    /// The grouped surface's Decimal field refuses the same values, so this refuses too.
    /// </summary>
    [Theory]
    [InlineData(1, "Max", "N/A")]
    [InlineData(2, "Sum", "1E+300")]
    [InlineData(2, "Avg", "1E+300")]
    public async Task DecimalAggregateNoDecimalHolds_IsAFieldErrorWithoutTheValue(int quoteId, string operation, string cellText)
    {
        var execution = await RunQuoteAsync(quoteId, $"top: _agg(value: {{ quote_lines: {{ column: price }} }} operation: {operation})");

        execution.Errors.Should().NotBeNullOrEmpty();
        var error = execution.Errors!.Should().ContainSingle().Subject;
        var chain = new List<Exception>();
        for (Exception? e = error; e != null; e = e.InnerException) chain.Add(e);
        chain.Should().NotContain(e => e is FormatException || e is OverflowException);
        chain.Select(e => e.Message).Should().NotContain(m => m.Contains(cellText, StringComparison.OrdinalIgnoreCase)
                                                               || m.Contains("1e300", StringComparison.OrdinalIgnoreCase));
        chain.Should().Contain(e => e.Message.Contains(AggregateNumberGraphType.TypeName));
    }

    /// <summary>Control: a numeric DECIMAL cell on the same column is a decimal string.</summary>
    [Fact]
    public async Task DecimalAggregateOfANumber_IsADecimalString()
    {
        var execution = await RunQuoteAsync(3, "top: _agg(value: { quote_lines: { column: price } } operation: Max)");

        execution.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        var top = doc.RootElement.GetProperty("data").GetProperty("quotes").GetProperty("data")[0].GetProperty("top");
        top.ValueKind.Should().Be(JsonValueKind.String);
        top.GetString().Should().Be("12.5");
    }

    /// <summary>
    /// The grouped surface's <c>Decimal</c> field (<c>ExactDecimalGraphType</c>) refuses the
    /// same cells as <c>_agg</c>, the same way: a field error naming no cell data, never the
    /// stock conversion message that embeds the text ('N/A' under MAX) nor a raw
    /// OverflowException (1e300 under MAX and SUM). Quote 3's group is the control: the same
    /// field, in the same response, still serializes a numeric cell.
    /// </summary>
    [Theory]
    [InlineData("_max", 2)]
    [InlineData("_sum", 1)]
    public async Task GroupedDecimalAggregateNoDecimalHolds_IsAFieldErrorWithoutTheValue(string operation, int refusedGroups)
    {
        var schema = DbSchema.FromModel(_model);
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ quote_linesAggregate(groupBy: [quote_id]) {{ quote_id {operation} {{ price }} }} }}";
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().HaveCount(refusedGroups);
        foreach (var error in execution.Errors!)
        {
            var chain = new List<Exception>();
            for (Exception? e = error; e != null; e = e.InnerException) chain.Add(e);
            chain.Should().NotContain(e => e is FormatException || e is OverflowException);
            chain.Select(e => e.Message).Should().NotContain(m => m.Contains("N/A", StringComparison.OrdinalIgnoreCase)
                                                                   || m.Contains("E+300", StringComparison.OrdinalIgnoreCase)
                                                                   || m.Contains("1e300", StringComparison.OrdinalIgnoreCase));
            chain.Should().Contain(e => e.Message.Contains("'Decimal'"));
        }

        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        var plain = doc.RootElement.GetProperty("data").GetProperty("quote_linesAggregate").EnumerateArray()
            .Single(g => g.GetProperty("quote_id").GetInt64() == 3);
        plain.GetProperty(operation).GetProperty("price").GetString().Should().Be("12.5");
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

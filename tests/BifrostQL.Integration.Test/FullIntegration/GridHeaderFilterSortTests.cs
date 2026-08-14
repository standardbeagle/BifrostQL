using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RootExecutionNode = GraphQL.Execution.RootExecutionNode;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// The grid's column-header menu (edit-db <c>DataTableColumnHeader</c>) offers sort
/// and filter commands per column. Both compile to a GraphQL document built by
/// <c>examples/edit-db/src/lib/query-builder.ts</c>: sorting to a
/// <c>&lt;table&gt;SortEnum</c> value, filtering to a variable whose DECLARED type comes
/// from <c>getGraphQlType(column.paramType)</c> and whose position is the column's
/// <c>FilterType&lt;T&gt;Input</c> field.
///
/// A declared type that does not match the filter field's type is a hard GraphQL
/// validation error — the grid shows no rows, for every value the user types. These
/// tests execute the client's exact document shapes against a live SQLite database so
/// the client's type table cannot drift from the server's emitted schema. The column
/// set deliberately spans every scalar the type mappers can produce (Int, Short, Byte,
/// BigInt, Decimal, Float, Boolean, DateTime, String), not just the Int/String pair a
/// convenience fixture would carry.
/// </summary>
[Collection("GridHeaderFilterSort")]
public class GridHeaderFilterSortTests : FullIntegrationTestBase, IAsyncLifetime
{
    private SqliteConnection? _keepAliveConnection;

    public async Task InitializeAsync()
    {
        var connectionString = "Data Source=bifrost_grid_header_test;Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(connectionString);
        await _keepAliveConnection.OpenAsync();

        var factory = new SqliteDbConnFactory(connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_keepAliveConnection != null)
            await _keepAliveConnection.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "DROP TABLE IF EXISTS widgets",
            "DROP TABLE IF EXISTS ledger",
            @"CREATE TABLE ledger (
                entry_id BIGINT PRIMARY KEY,
                note TEXT NOT NULL
            )",
            @"CREATE TABLE widgets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                qty INTEGER,
                small_qty SMALLINT,
                tiny_flag TINYINT,
                big_ref BIGINT,
                price DECIMAL(10,2),
                ratio REAL,
                active BOOLEAN,
                created_at DATETIME
            )",
        };

        foreach (var sql in statements)
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var cmd = new SqliteCommand(
            @"INSERT INTO widgets (name, qty, small_qty, tiny_flag, big_ref, price, ratio, active, created_at) VALUES
                ('alpha', 10, 5, 1, 9007199254740993, 19.99, 1.5, 1, '2024-01-15 08:00:00'),
                ('beta',  20, 6, 0, 9007199254740994, 29.99, 2.5, 0, '2024-02-15 08:00:00'),
                ('gamma', 30, 7, 1, 9007199254740995, 39.99, 3.5, 1, '2024-03-15 08:00:00')",
            (SqliteConnection)conn);
        await cmd.ExecuteNonQueryAsync();

        var ledger = new SqliteCommand(
            @"INSERT INTO ledger (entry_id, note) VALUES
                (9007199254740993, 'first'),
                (9007199254740995, 'third')",
            (SqliteConnection)conn);
        await ledger.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The client's grid query envelope, with one column-filter variable — the exact
    /// text <c>buildListQuery</c> emits for a single active header filter.
    /// </summary>
    private static string FilterQuery(string column, string op, string declaredType) =>
        $"query Getwidgets($sort: [widgetsSortEnum!], $limit: Int, $offset: Int , $cf_{column}_0: {declaredType}) " +
        $"{{ widgets(sort: $sort limit: $limit offset: $offset filter: {{{column}: {{{op}: $cf_{column}_0}}}}) " +
        "{ total offset limit data { id name } } }";

    private static Dictionary<string, object?> FilterVariables(string column, object? value, string sort = "id_asc") => new()
    {
        ["sort"] = new List<object?> { sort },
        ["limit"] = 50,
        ["offset"] = 0,
        [$"cf_{column}_0"] = value,
    };

    private async Task<int> RunFilterAsync(string column, string op, string declaredType, object? value)
    {
        var result = await ExecuteQueryAsync(FilterQuery(column, op, declaredType), FilterVariables(column, value));
        result.Errors.Should().BeNullOrEmpty(
            $"the header filter on '{column}' must produce a document the server accepts");
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        var page = data!["widgets"] as Dictionary<string, object?>;
        return Convert.ToInt32(page!["total"]);
    }

    // The client's paramType -> declared GraphQL variable type table
    // (getGraphQlType in query-builder.ts). Each row is a column the header menu
    // offers a filter for, and the type its variable is declared as.
    public static TheoryData<string, string, string, object?, int> FilterCases() => new()
    {
        // column,       operator,     declared type, value,                 expected rows
        { "name",        "_contains",  "String",      "alph",                1 },
        { "name",        "_eq",        "String",      "beta",                1 },
        { "qty",         "_gte",       "Int",         20,                    2 },
        { "small_qty",   "_eq",        "Short",       6,                     1 },
        { "tiny_flag",   "_eq",        "Byte",        1,                     2 },
        { "big_ref",     "_eq",        "BigInt",      "9007199254740995",    1 },
        { "price",       "_gt",        "Decimal",     "20.00",               2 },
        { "ratio",       "_lt",        "Float",       2.0,                   1 },
        { "active",      "_eq",        "Boolean",     true,                  2 },
        // The date control is date-only, so query-builder widens the bound to a
        // day edge — the value here is exactly what `dayBoundary(…, 'start')` emits.
        { "created_at",  "_gte",       "DateTime",    "2024-02-01T00:00:00.000", 2 },
    };

    [Theory]
    [MemberData(nameof(FilterCases))]
    public async Task HeaderFilter_RunsAgainstTheServersFilterInput(
        string column, string op, string declaredType, object? value, int expected)
    {
        var total = await RunFilterAsync(column, op, declaredType, value);
        total.Should().Be(expected);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("qty")]
    [InlineData("small_qty")]
    [InlineData("tiny_flag")]
    [InlineData("big_ref")]
    [InlineData("price")]
    [InlineData("ratio")]
    [InlineData("active")]
    [InlineData("created_at")]
    public async Task HeaderSort_AscAndDesc_AreAcceptedForEveryColumn(string column)
    {
        foreach (var direction in new[] { "asc", "desc" })
        {
            var query = "query Getwidgets($sort: [widgetsSortEnum!], $limit: Int, $offset: Int ) " +
                        "{ widgets(sort: $sort limit: $limit offset: $offset ) { total offset limit data { id name } } }";
            var result = await ExecuteQueryAsync(query, new Dictionary<string, object?>
            {
                ["sort"] = new List<object?> { $"{column}_{direction}" },
                ["limit"] = 50,
                ["offset"] = 0,
            });
            result.Errors.Should().BeNullOrEmpty($"the header offers {direction} sort on '{column}'");
        }
    }

    /// <summary>
    /// `_between` declares TWO variables of the column type and passes them as a list
    /// — a distinct position from `_eq` (the field is `[T!]`, not `T`), so it needs its
    /// own coverage.
    /// </summary>
    [Fact]
    public async Task HeaderFilter_Between_BindsBothBoundsAsTheColumnType()
    {
        var query = "query Getwidgets($sort: [widgetsSortEnum!], $limit: Int, $offset: Int , $cf_qty_0_lo: Int, $cf_qty_0_hi: Int) " +
                    "{ widgets(sort: $sort limit: $limit offset: $offset filter: {qty: {_between: [$cf_qty_0_lo, $cf_qty_0_hi]}}) " +
                    "{ total offset limit data { id name } } }";
        var result = await ExecuteQueryAsync(query, new Dictionary<string, object?>
        {
            ["sort"] = new List<object?> { "id_asc" },
            ["limit"] = 50,
            ["offset"] = 0,
            ["cf_qty_0_lo"] = 15,
            ["cf_qty_0_hi"] = 35,
        });

        result.Errors.Should().BeNullOrEmpty();
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        var page = data!["widgets"] as Dictionary<string, object?>;
        Convert.ToInt32(page!["total"]).Should().Be(2);
    }

    /// <summary>
    /// A date-only bound widens to the whole local day: `_eq` becomes a `_between`
    /// over the day, `_neq` a `_nbetween`. Sent as a bare midnight instant instead,
    /// "on 15 Jan" would match nothing (no row is recorded at exactly 00:00) — the
    /// header's most obvious date command would silently return an empty grid.
    /// </summary>
    [Theory]
    [InlineData("_between", 1)]
    [InlineData("_nbetween", 2)]
    public async Task HeaderFilter_OnADay_SpansThatWholeDay(string op, int expected)
    {
        var query = $"query Getwidgets($sort: [widgetsSortEnum!], $limit: Int, $offset: Int , $cf_created_at_0_lo: DateTime, $cf_created_at_0_hi: DateTime) " +
                    $"{{ widgets(sort: $sort limit: $limit offset: $offset filter: {{created_at: {{{op}: [$cf_created_at_0_lo, $cf_created_at_0_hi]}}}}) " +
                    "{ total offset limit data { id name } } }";
        var result = await ExecuteQueryAsync(query, new Dictionary<string, object?>
        {
            ["sort"] = new List<object?> { "id_asc" },
            ["limit"] = 50,
            ["offset"] = 0,
            ["cf_created_at_0_lo"] = "2024-01-15T00:00:00.000",
            ["cf_created_at_0_hi"] = "2024-01-15T23:59:59.999",
        });

        result.Errors.Should().BeNullOrEmpty();
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        var page = data!["widgets"] as Dictionary<string, object?>;
        Convert.ToInt32(page!["total"]).Should().Be(expected);
    }

    /// <summary>
    /// A bigint bound past 2^53 must survive the wire EXACTLY. JSON has one numeric
    /// type — a double — so a browser can only send such a value as text; routed
    /// through a number, 9007199254740993 arrives as ...992 and matches a different
    /// row while the header still shows the typed bound.
    /// </summary>
    [Theory]
    [InlineData("9007199254740993", "alpha")]
    [InlineData("9007199254740995", "gamma")]
    public async Task HeaderFilter_BigIntBeyondDoublePrecision_MatchesTheExactRow(string value, string expectedName)
    {
        var result = await ExecuteQueryAsync(FilterQuery("big_ref", "_eq", "BigInt"), FilterVariables("big_ref", value));

        result.Errors.Should().BeNullOrEmpty();
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        var page = data!["widgets"] as Dictionary<string, object?>;
        Convert.ToInt32(page!["total"]).Should().Be(1);
        var rows = (System.Collections.IList)page["data"]!;
        ((Dictionary<string, object?>)rows[0]!)["name"].Should().Be(expectedName);
    }

    /// <summary>
    /// A decimal bound keeps every digit it was given. Through a double, an exact
    /// fixed-point value silently becomes its nearest representable neighbour.
    /// </summary>
    [Fact]
    public async Task HeaderFilter_DecimalAsText_ComparesExactly()
    {
        var total = await RunFilterAsync("price", "_gte", "Decimal", "29.99");
        total.Should().Be(2);
    }

    /// <summary>
    /// The grid's ROW LINK, not a filter: opening a row builds the same single-row
    /// lookup <c>buildSingleRowQuery</c> emits, binding the primary key through the
    /// column's own scalar. On a bigint-keyed table the client sends that key as
    /// text (row-id.ts carries key values as decimal strings so a URL round-trip
    /// cannot round them), so a number-only BigInt scalar made every such row
    /// unreachable — the filter fix and this share one cause.
    /// </summary>
    [Theory]
    [InlineData("9007199254740993", "first")]
    [InlineData("9007199254740995", "third")]
    public async Task RowLink_BigIntPrimaryKeyAsText_OpensTheExactRow(string key, string expectedNote)
    {
        const string query = "query GetSingleRow_ledger($pk_entry_id: BigInt) " +
                             "{ value: ledger(filter: {entry_id: {_eq: $pk_entry_id}}) { data { entry_id note } } }";
        var result = await ExecuteQueryAsync(query, new Dictionary<string, object?> { ["pk_entry_id"] = key });

        result.Errors.Should().BeNullOrEmpty();
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        var page = data!["value"] as Dictionary<string, object?>;
        var rows = (System.Collections.IList)page!["data"]!;
        rows.Count.Should().Be(1);
        ((Dictionary<string, object?>)rows[0]!)["note"].Should().Be(expectedNote);
    }

    /// <summary>
    /// `_null` is emitted as a literal, not a variable, so it must work on every column
    /// kind the menu offers it for.
    /// </summary>
    [Fact]
    public async Task HeaderFilter_IsNull_MatchesNothingWhenEveryRowHasAValue()
    {
        var query = "query Getwidgets($sort: [widgetsSortEnum!], $limit: Int, $offset: Int ) " +
                    "{ widgets(sort: $sort limit: $limit offset: $offset filter: {price: {_null: true}}) " +
                    "{ total offset limit data { id name } } }";
        var result = await ExecuteQueryAsync(query, new Dictionary<string, object?>
        {
            ["sort"] = new List<object?> { "id_asc" },
            ["limit"] = 50,
            ["offset"] = 0,
        });

        result.Errors.Should().BeNullOrEmpty();
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        var page = data!["widgets"] as Dictionary<string, object?>;
        Convert.ToInt32(page!["total"]).Should().Be(0);
    }
}

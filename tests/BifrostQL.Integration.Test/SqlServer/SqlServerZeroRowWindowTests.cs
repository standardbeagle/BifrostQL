using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.FullIntegration;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQLParser;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Integration.Test.SqlServer;

/// <summary>
/// <c>limit: 0</c> is an empty page with its real total. SQL Server refuses
/// <c>FETCH NEXT 0 ROWS ONLY</c> (error 10744), so a zero window that reached the
/// dialect's OFFSET/FETCH failed the whole document instead. Each fact runs one
/// statement shape that windows rows against a live SQL Server: the root row read, the
/// link join-id window that re-pages the parent, a grouped aggregate, and a flat
/// (non-paged) collection.
/// </summary>
[Collection("SqlServerZeroRowWindow")]
public sealed class SqlServerZeroRowWindowTests : FullIntegrationTestBase, IAsyncLifetime
{
    private string? _testDbName;
    private IDbConnFactory? _factory;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        if (string.IsNullOrEmpty(masterConnString))
            return;

        _testDbName = $"BifrostZeroWindow_{Guid.NewGuid():N}";
        await using (var master = new SqlConnection(masterConnString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{_testDbName}]", master);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(masterConnString) { InitialCatalog = _testDbName };
        _factory = new SqlServerDbConnFactory(builder.ConnectionString);
        await base.InitializeAsync(_factory, conn => ExecAsync(conn, """
            CREATE TABLE customers (id INT PRIMARY KEY, name NVARCHAR(50) NOT NULL);
            CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT NOT NULL, region NVARCHAR(20) NOT NULL,
                CONSTRAINT FK_orders_customers FOREIGN KEY (customer_id) REFERENCES customers(id));
            """), conn => ExecAsync(conn, """
            INSERT INTO customers VALUES (1, N'a'), (2, N'b'), (3, N'c');
            INSERT INTO orders VALUES (10, 1, N'north'), (11, 1, N'south'), (12, 2, N'north');
            """));
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER")!;
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(masterConnString);
        await master.OpenAsync();
        await using var drop = new SqlCommand($"DROP DATABASE IF EXISTS [{_testDbName}]", master);
        await drop.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task RootRead_ZeroLimit_ReturnsAnEmptyPageWithTheTotal()
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        var page = await RunAsync("{ customers(limit: 0) { total data { id } } }", "customers");

        page.GetProperty("total").GetInt32().Should().Be(3, "the total counts every row, not the empty window");
        page.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [SkippableFact]
    public async Task RootRead_ZeroLimitWithLink_ReturnsAnEmptyPageWithTheTotal()
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        // The link's join-id statement re-pages the parent through the same window.
        var page = await RunAsync(
            "{ customers(limit: 0) { total data { id orders { total data { id } } } } }", "customers");

        page.GetProperty("total").GetInt32().Should().Be(3);
        page.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [SkippableFact]
    public async Task RootRead_LinkControl_ReturnsChildren()
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        // Paired control: the same document with a non-zero window returns the linked rows,
        // so the zero-window fact above is not empty for a broken link.
        var page = await RunAsync(
            "{ customers(limit: 1, sort: [id_asc]) { total data { id orders { total data { id } } } } }", "customers");

        var first = page.GetProperty("data")[0];
        first.GetProperty("orders").GetProperty("total").GetInt32().Should().Be(2);
    }

    // A nested paged collection reads its per-parent total off the window's __total column;
    // an empty window dropped every row carrying it, so total read 0.
    [SkippableTheory]
    [InlineData("limit: 0", new[] { 2, 1, 0 }, new[] { 0, 0, 0 })]
    [InlineData("offset: 1, limit: 0", new[] { 2, 1, 0 }, new[] { 0, 0, 0 })]
    [InlineData("offset: 1, limit: 5", new[] { 2, 1, 0 }, new[] { 1, 0, 0 })]
    [InlineData("limit: 1", new[] { 2, 1, 0 }, new[] { 1, 1, 0 })]
    public async Task NestedPagedCollection_EmptyWindow_ReportsEachParentsTotal(string arguments, int[] totals, int[] rowCounts)
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        var page = await RunAsync(
            $"{{ customers(sort: [id_asc]) {{ data {{ id orders({arguments}, sort: [id_asc]) {{ total data {{ id }} }} }} }} }}", "customers");

        var orders = page.GetProperty("data").EnumerateArray().Select(c => c.GetProperty("orders")).ToList();
        orders.Select(o => o.GetProperty("total").GetInt32()).Should().Equal(totals);
        orders.Select(o => o.GetProperty("data").GetArrayLength()).Should().Equal(rowCounts);
    }

    [SkippableFact]
    public async Task GroupedAggregate_ZeroLimit_ReturnsNoGroups()
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        var groups = await RunAsync("{ ordersAggregate(groupBy: [region], limit: 0) { region _count } }", "ordersAggregate");

        groups.GetArrayLength().Should().Be(0);
    }

    [SkippableFact]
    public async Task FlatCollection_ZeroLimit_ReturnsNoRows()
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        // An explicit `_join_` is the flat (non-paged) collection: its LIMIT is global.
        var results = await ExecuteTreeAsync(
            "query { customers { data { id j:_join_orders(on: { id: { _eq: customer_id } }, limit: 0) { id } } } }");

        results["customers"].data.Should().HaveCount(3);
        results["customers->j"].data.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task FlatCollection_Control_ReturnsRows()
    {
        Skip.If(_factory is null, "BIFROST_TEST_SQLSERVER not set");

        var results = await ExecuteTreeAsync(
            "query { customers { data { id j:_join_orders(on: { id: { _eq: customer_id } }, limit: 2) { id } } } }");

        results["customers->j"].data.Should().HaveCount(2);
    }

    private async Task<JsonElement> RunAsync(string query, string field)
    {
        var result = await ExecuteQueryAsync(query);
        var json = JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement;
        json.TryGetProperty("errors", out var errors).Should().BeFalse(errors.ToString());
        return json.GetProperty("data").GetProperty(field);
    }

    private async Task<IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>> ExecuteTreeAsync(string document)
    {
        var ctx = new SqlContext();
        await new SqlVisitor().VisitAsync(Parser.Parse(document), ctx);
        var query = ctx.GetFinalQueries(Model).Should().ContainSingle().Subject;
        return QueryExecutor.Execute(query, Model, _factory!);
    }

    private static async Task ExecAsync(System.Data.Common.DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

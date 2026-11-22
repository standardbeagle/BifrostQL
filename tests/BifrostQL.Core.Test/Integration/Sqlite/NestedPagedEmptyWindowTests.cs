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
/// A nested paged collection reads its per-parent total off the window's <c>__total</c>
/// column. A window that keeps no row for a parent (<c>limit: 0</c>, or an offset at or
/// past that parent's row count) dropped the only rows carrying it, so the total read 0
/// while the root path reported the real count. Ann has three orders, bob one, cy none.
/// </summary>
public sealed class NestedPagedEmptyWindowTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_nested_paged_empty_window_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS users");
        await Exec("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), amount INTEGER NOT NULL)");
        await Exec("INSERT INTO users(id, name) VALUES (1, 'ann'), (2, 'bob'), (3, 'cy')");
        await Exec("INSERT INTO orders(id, user_id, amount) VALUES (10, 1, 7), (11, 1, 8), (12, 1, 9), (20, 2, 5)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    /// <summary>Each user's <c>orders</c> page, in user id order.</summary>
    private async Task<List<(int Total, int[] Ids)>> OrderPagesAsync(string ordersArguments)
    {
        var schema = DbSchema.FromModel(_model);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ users(sort: [id_asc]) {{ data {{ id orders({ordersArguments}) {{ total data {{ id }} }} }} }} }}";
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });

        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeFalse(errors.ToString());
        return doc.RootElement.GetProperty("data").GetProperty("users").GetProperty("data").EnumerateArray()
            .Select(user => user.GetProperty("orders"))
            .Select(page => (
                page.GetProperty("total").GetInt32(),
                page.GetProperty("data").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToArray()))
            .ToList();
    }

    [Fact]
    public async Task ZeroLimit_ReportsEachParentsTotalWithNoRows()
    {
        var pages = await OrderPagesAsync("limit: 0");

        pages.Select(p => p.Total).Should().Equal(3, 1, 0);
        pages.Should().AllSatisfy(p => p.Ids.Should().BeEmpty());
    }

    [Fact]
    public async Task NonZeroLimit_Control_ReportsTheSameTotalsWithRows()
    {
        var pages = await OrderPagesAsync("limit: 1, sort: [id_asc]");

        pages.Select(p => p.Total).Should().Equal(3, 1, 0);
        pages.Select(p => p.Ids).Should().BeEquivalentTo(
            new[] { new[] { 10 }, new[] { 20 }, Array.Empty<int>() }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task OffsetPastAParentsRows_ReportsItsTotalWithNoRows()
    {
        // Ann keeps her third order; bob's one order sits before the window.
        var pages = await OrderPagesAsync("offset: 2, limit: 5, sort: [id_asc]");

        pages.Select(p => p.Total).Should().Equal(3, 1, 0);
        pages.Select(p => p.Ids).Should().BeEquivalentTo(
            new[] { new[] { 12 }, Array.Empty<int>(), Array.Empty<int>() }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task ZeroLimitWithOffset_ReportsEachParentsTotalWithNoRows()
    {
        var pages = await OrderPagesAsync("offset: 1, limit: 0");

        pages.Select(p => p.Total).Should().Equal(3, 1, 0);
        pages.Should().AllSatisfy(p => p.Ids.Should().BeEmpty());
    }
}

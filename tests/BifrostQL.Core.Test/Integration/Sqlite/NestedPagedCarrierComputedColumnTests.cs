using System.Collections.Concurrent;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A nested paged collection whose window keeps no row for a parent still reads one carrier
/// row for that parent, outside the window, only so its total reaches the reader. Provider
/// computed columns ran for those carrier rows too: work for rows no caller sees. Ann has
/// three orders, bob one, cy none.
/// </summary>
public sealed class NestedPagedCarrierComputedColumnTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_nested_paged_carrier_computed_test;Mode=Memory;Cache=Shared";
    private const string ProviderName = "order-label";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private sealed class RecordingProvider : IComputedColumnProvider
    {
        public readonly ConcurrentBag<long> RowIds = new();
        public string Name => ProviderName;

        public ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
        {
            var id = Convert.ToInt64(context.Row["id"]);
            RowIds.Add(id);
            return ValueTask.FromResult<object?>($"order-{id}");
        }
    }

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

        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[]
        {
            $"main.orders {{ computed-plugin: label:String:{ProviderName}:depends=id }}",
        })).LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<(List<(int Total, string[] Labels)> Pages, long[] ComputedFor)> OrderPagesAsync(string ordersArguments)
    {
        var schema = DbSchema.FromModel(_model);
        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IComputedColumnProviders>(new ComputedColumnProviders(new IComputedColumnProvider[] { recorder }));
        await using var provider = services.BuildServiceProvider();

        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ users(sort: [id_asc]) {{ data {{ id orders({ordersArguments}) {{ total data {{ id label }} }} }} }} }}";
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });

        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeFalse(errors.ToString());
        var pages = doc.RootElement.GetProperty("data").GetProperty("users").GetProperty("data").EnumerateArray()
            .Select(user => user.GetProperty("orders"))
            .Select(page => (
                page.GetProperty("total").GetInt32(),
                page.GetProperty("data").EnumerateArray().Select(o => o.GetProperty("label").GetString()!).ToArray()))
            .ToList();
        return (pages, recorder.RowIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task ZeroLimit_ComputesNothing()
    {
        var (pages, computedFor) = await OrderPagesAsync("limit: 0");

        pages.Select(p => p.Total).Should().Equal(3, 1, 0);
        computedFor.Should().BeEmpty("every row read is a total carrier outside the window");
    }

    [Fact]
    public async Task OffsetPastAParentsRows_ComputesOnlyTheWindow()
    {
        // Ann keeps her third order; bob's one order is a carrier before the window.
        var (pages, computedFor) = await OrderPagesAsync("offset: 2, limit: 5, sort: [id_asc]");

        pages.Select(p => p.Total).Should().Equal(3, 1, 0);
        pages.Select(p => p.Labels).Should().BeEquivalentTo(
            new[] { new[] { "order-12" }, Array.Empty<string>(), Array.Empty<string>() }, o => o.WithStrictOrdering());
        computedFor.Should().Equal(12);
    }

    [Fact]
    public async Task NonZeroLimit_Control_ComputesEachRowOnThePage()
    {
        var (pages, computedFor) = await OrderPagesAsync("limit: 1, sort: [id_asc]");

        pages.Select(p => p.Labels).Should().BeEquivalentTo(
            new[] { new[] { "order-10" }, new[] { "order-20" }, Array.Empty<string>() }, o => o.WithStrictOrdering());
        computedFor.Should().Equal(10, 20);
    }
}

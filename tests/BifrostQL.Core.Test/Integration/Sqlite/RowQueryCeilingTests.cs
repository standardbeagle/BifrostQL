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
/// H7 follow-up (01M1MT73CNJM4B8PTMQV846P9Y), end to end: with <c>max-query-rows</c>
/// below the 100-row default window and no <c>limit</c>, a root row query returns the
/// ceiling's worth of rows — not the dialect default the ceiling never saw. The
/// client's <c>limit</c> can only narrow: <c>0</c> is an empty page, <c>2</c> is two
/// rows. The SQL-text half of this contract is RowQueryDefaultWindowCeilingTests.
/// </summary>
public sealed class RowQueryCeilingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_row_query_ceiling_test;Mode=Memory;Cache=Shared";
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
        var values = string.Join(", ", Enumerable.Range(1, RowCount).Select(i => $"({i}, {i})"));
        await Exec($"INSERT INTO events(id, amount) VALUES {values}");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Rules));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<List<int>> IdsAsync(string query)
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
        return doc.RootElement.GetProperty("data").GetProperty("events").GetProperty("data")
            .EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
    }

    [Fact]
    public async Task NoLimit_ReturnsCeilingRows_NotTheDefaultWindow()
    {
        var ids = await IdsAsync("{ events(sort: [id_asc]) { data { id } } }");

        ids.Should().Equal(Enumerable.Range(1, Ceiling),
            "an unspecified limit must pass through the ceiling instead of taking the dialect's 100-row default");
    }

    [Fact]
    public async Task ClientLimit_MayNarrow_ButNeverRaisesTheCeiling()
    {
        (await IdsAsync("{ events(sort: [id_asc], limit: 2) { data { id } } }")).Should().Equal(1, 2);
        (await IdsAsync("{ events(sort: [id_asc], limit: 0) { data { id } } }")).Should().BeEmpty(
            "limit: 0 is an empty page, not the default window");
        (await IdsAsync($"{{ events(sort: [id_asc], limit: {RowCount}) {{ data {{ id }} }} }}"))
            .Should().HaveCount(Ceiling, "the ceiling is a server ceiling; a client may only narrow it");
    }
}

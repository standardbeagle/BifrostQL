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
/// GraphQL merges fields that share a response key (alias, else name) into one field
/// whose selection is the union. The query model built one node per occurrence, and the
/// resolver bound the first, so a column selected only by a later occurrence came back
/// null. Each fact compares a split selection against the same selection written once.
/// </summary>
public sealed class DuplicateResponseKeyMergeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_duplicate_response_key_test;Mode=Memory;Cache=Shared";

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
        await Exec("INSERT INTO users(id, name) VALUES (1, 'ann'), (2, 'bob')");
        await Exec("INSERT INTO orders(id, user_id, amount) VALUES (10, 1, 7), (11, 1, 8), (12, 2, 9)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<JsonElement> ExecuteAsync(string query)
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

        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        return doc.RootElement.Clone();
    }

    private async Task<string> RunAsync(string query)
    {
        var root = await ExecuteAsync(query);
        root.TryGetProperty("errors", out var errors).Should().BeFalse(errors.ToString());
        return root.GetProperty("data").GetRawText();
    }

    private const string Whole = "{ users(sort: [id_asc]) { data { id name } } }";

    [Fact]
    public async Task InlineRootDuplicate_ReturnsTheUnionOfBothSelections()
    {
        var whole = await RunAsync(Whole);
        whole.Should().Contain("\"bob\"", "control: the single-field form reads names");

        var split = await RunAsync(
            "{ users(sort: [id_asc]) { data { id } } users(sort: [id_asc]) { data { name } } }");

        split.Should().Be(whole);
    }

    [Fact]
    public async Task RootSpreadPlusInline_ReturnsTheUnionOfBothSelections()
    {
        var split = await RunAsync(
            """
            query { users(sort: [id_asc]) { data { id } } ...F }
            fragment F on database { users(sort: [id_asc]) { data { name } } }
            """);

        split.Should().Be(await RunAsync(Whole));
    }

    [Fact]
    public async Task TwoRootSpreads_ReturnTheUnionOfBothSelections()
    {
        var split = await RunAsync(
            """
            query { ...A ...B }
            fragment A on database { users(sort: [id_asc]) { data { id } } }
            fragment B on database { users(sort: [id_asc]) { data { name } } }
            """);

        split.Should().Be(await RunAsync(Whole));
    }

    [Fact]
    public async Task NestedDuplicate_ReturnsTheUnionOfBothSelections()
    {
        var split = await RunAsync("{ users(sort: [id_asc]) { data { id } data { name } } }");

        split.Should().Be(await RunAsync(Whole));
    }

    [Fact]
    public async Task NestedLinkDuplicate_ReturnsTheUnionOfBothSelections()
    {
        const string wholeLink =
            "{ users(sort: [id_asc]) { data { id orders(sort: [id_asc]) { data { id amount } } } } }";
        var whole = await RunAsync(wholeLink);
        whole.Should().Contain("\"amount\":8", "control: the single-link form reads amounts");

        var split = await RunAsync(
            "{ users(sort: [id_asc]) { data { id orders(sort: [id_asc]) { data { id } } orders(sort: [id_asc]) { data { amount } } } } }");

        split.Should().Be(whole);
    }

    [Fact]
    public async Task SameKeyWithDifferentArguments_IsRejectedByValidation()
    {
        var root = await ExecuteAsync(
            "{ users(limit: 1) { data { id } } users(limit: 2) { data { name } } }");

        root.TryGetProperty("errors", out var errors).Should().BeTrue();
        errors.ToString().Should().Contain("conflict");
        root.TryGetProperty("data", out var data).Should().BeFalse(data.ToString());
    }
}

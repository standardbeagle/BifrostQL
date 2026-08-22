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
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// SQLite has no datetime storage class: a column declared DATETIME stores TEXT
/// and materializes as a STRING. The stock DateTime scalar threw
/// INVALID_OPERATION on it, so every read of such a column failed — the chat
/// demo's messages.created_at turned each history reload into an error banner.
/// DbDateTimeGraphType must serialize the database string; garbage must still
/// fail loudly rather than pass through as a mistyped value.
/// </summary>
public sealed class DateTimeColumnReadTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_datetime_read_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS notes");
        await Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL, created_at DATETIME NULL)");
        await Exec("INSERT INTO notes (id, body, created_at) VALUES (1, 'first', '2026-07-02 14:40:00')");
        await Exec("INSERT INTO notes (id, body, created_at) VALUES (2, 'second', NULL)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DatetimeDeclaredColumn_ReadsAsARoundTrippableValue_NotAResolveError()
    {
        var schema = DbSchema.FromModel(_model);
        var executor = new DocumentExecuter();
        var result = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = "{ notes(sort: [id_asc]) { data { id created_at } } }";
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(
                    _model, schema, new QueryTransformerService(new FilterTransformersWrap
                    {
                        Transformers = Array.Empty<IFilterTransformer>(),
                    })),
            });
        });

        result.Errors.Should().BeNullOrEmpty(
            "a DATETIME-declared SQLite column materializes as a string and must still resolve");
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var rows = doc.RootElement.GetProperty("data").GetProperty("notes").GetProperty("data");
        DateTime.Parse(rows[0].GetProperty("created_at").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().Be(new DateTime(2026, 7, 2, 14, 40, 0),
                "the database string round-trips as the same instant");
        rows[1].GetProperty("created_at").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

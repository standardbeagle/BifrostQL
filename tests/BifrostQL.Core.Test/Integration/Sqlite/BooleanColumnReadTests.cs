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
/// SQLite has no boolean storage class: a column declared BOOLEAN or BIT stores
/// INTEGER 0/1 and Microsoft.Data.Sqlite materializes it as a <see cref="long"/>.
/// The schema advertises such a column as <c>Boolean</c>, so the scalar must turn
/// the database integer into true/false; any other integer must still fail
/// loudly rather than pass through as a mistyped value. The narrow integer
/// declarations (TINYINT → Byte, SMALLINT → Short) also materialize as long.
/// </summary>
public sealed class BooleanColumnReadTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_boolean_read_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS flags");
        await Exec("""
            CREATE TABLE flags (
                id INTEGER PRIMARY KEY,
                active BOOLEAN NULL,
                archived BIT NOT NULL,
                level TINYINT NOT NULL,
                rank SMALLINT NOT NULL
            )
            """);
        await Exec("INSERT INTO flags VALUES (1, 1, 0, 200, -300)");
        await Exec("INSERT INTO flags VALUES (2, 0, 1, 0, 32000)");
        await Exec("INSERT INTO flags VALUES (3, NULL, 0, 7, 0)");
        await Exec("INSERT INTO flags VALUES (4, 2, 0, 0, 0)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> Query(string query)
    {
        var schema = DbSchema.FromModel(_model);
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
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
    }

    [Fact]
    public async Task BooleanAndBitColumns_ReadAsJsonBooleans()
    {
        var result = await Query("{ flags(filter: { id: { _lt: 4 } }, sort: [id_asc]) { data { id active archived level rank } } }");

        result.Errors.Should().BeNullOrEmpty(
            "a BOOLEAN-declared SQLite column materializes as a long and must still resolve");
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var rows = doc.RootElement.GetProperty("data").GetProperty("flags").GetProperty("data");
        rows[0].GetProperty("active").GetBoolean().Should().BeTrue();
        rows[0].GetProperty("archived").GetBoolean().Should().BeFalse();
        rows[1].GetProperty("active").GetBoolean().Should().BeFalse();
        rows[1].GetProperty("archived").GetBoolean().Should().BeTrue();
        rows[2].GetProperty("active").ValueKind.Should().Be(JsonValueKind.Null);
        rows[0].GetProperty("level").GetInt32().Should().Be(200);
        rows[0].GetProperty("rank").GetInt32().Should().Be(-300);
        rows[1].GetProperty("rank").GetInt32().Should().Be(32000);
    }

    [Fact]
    public async Task BooleanColumn_HoldingAnIntegerOtherThanZeroOrOne_FailsLoudly()
    {
        var result = await Query("{ flags(filter: { id: { _eq: 4 } }) { data { id active } } }");

        result.Errors.Should().NotBeNullOrEmpty(
            "2 is not a boolean; passing it through or coercing it to true would hide corrupt data");
    }
}

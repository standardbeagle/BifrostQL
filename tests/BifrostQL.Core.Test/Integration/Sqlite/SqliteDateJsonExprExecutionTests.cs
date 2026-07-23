using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests that execute the SQLite lowering of <see cref="SqlExpr.DateAdd"/> and
/// <see cref="SqlExpr.JsonGet"/> against a real in-memory SQLite database and assert the RETURNED
/// VALUE (not just the emitted SQL text). This proves the lowered date-arithmetic and JSON path
/// extraction actually compute the right result end to end, and that the numeric amount binds as a
/// real parameter through <see cref="SqlParameterCollection"/>.
/// </summary>
public sealed class SqliteDateJsonExprExecutionTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private readonly SqliteDialect _dialect = SqliteDialect.Instance;

    private static IDbTable EventsTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Events", t => t
                .WithPrimaryKey("Id", "int")
                .WithColumn("StartAt", "datetime")
                .WithColumn("Payload", "nvarchar"))
            .Build();
        return model.GetTableFromDbName("Events");
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        await using (var cmd = new SqliteCommand(@"
            CREATE TABLE Events (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartAt TEXT NOT NULL,
                Payload TEXT NOT NULL
            )", _connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new SqliteCommand(
            "INSERT INTO Events (StartAt, Payload) VALUES (@start, @payload)", _connection))
        {
            cmd.Parameters.AddWithValue("@start", "2020-01-01 00:00:00");
            cmd.Parameters.AddWithValue("@payload", "{\"user\":{\"name\":\"neo\",\"id\":42},\"active\":true}");
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>Binds every parameter the lowering registered onto the command.</summary>
    private static void BindParameters(SqliteCommand cmd, SqlParameterCollection parameters)
    {
        foreach (var p in parameters.Parameters)
            cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
    }

    [Fact]
    public async Task DateAdd_AddsDays_ReturnsRealComputedDate()
    {
        var parameters = new SqlParameterCollection();
        var expr = new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(3));

        var loweredExpr = _dialect.LowerExpression(expr, EventsTable(), parameters);
        var sql = $"SELECT {loweredExpr} AS Result FROM {_dialect.EscapeIdentifier("Events")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Id")} = 1";

        await using var cmd = new SqliteCommand(sql, _connection);
        BindParameters(cmd, parameters);
        var result = (string)(await cmd.ExecuteScalarAsync())!;

        // 2020-01-01 + 3 days, computed by SQLite's datetime() from the lowered expression.
        result.Should().Be("2020-01-04 00:00:00");
    }

    [Fact]
    public async Task DateAdd_NegativeAmount_SubtractsDays()
    {
        var parameters = new SqlParameterCollection();
        var expr = new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(-2));

        var loweredExpr = _dialect.LowerExpression(expr, EventsTable(), parameters);
        var sql = $"SELECT {loweredExpr} AS Result FROM {_dialect.EscapeIdentifier("Events")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Id")} = 1";

        await using var cmd = new SqliteCommand(sql, _connection);
        BindParameters(cmd, parameters);
        var result = (string)(await cmd.ExecuteScalarAsync())!;

        result.Should().Be("2019-12-30 00:00:00");
    }

    [Fact]
    public async Task JsonGet_NestedPath_ReturnsRealScalarValue()
    {
        var parameters = new SqlParameterCollection();
        var expr = new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "name"));

        var loweredExpr = _dialect.LowerExpression(expr, EventsTable(), parameters);
        var sql = $"SELECT {loweredExpr} AS Result FROM {_dialect.EscapeIdentifier("Events")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Id")} = 1";

        await using var cmd = new SqliteCommand(sql, _connection);
        BindParameters(cmd, parameters);
        var result = await cmd.ExecuteScalarAsync();

        // Extracted from {"user":{"name":"neo",...}} at $.user.name.
        result.Should().Be("neo");
    }

    [Fact]
    public async Task JsonGet_NumericLeaf_ReturnsRealValue()
    {
        var parameters = new SqlParameterCollection();
        var expr = new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "id"));

        var loweredExpr = _dialect.LowerExpression(expr, EventsTable(), parameters);
        var sql = $"SELECT {loweredExpr} AS Result FROM {_dialect.EscapeIdentifier("Events")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Id")} = 1";

        await using var cmd = new SqliteCommand(sql, _connection);
        BindParameters(cmd, parameters);
        var result = await cmd.ExecuteScalarAsync();

        // json_extract yields the JSON number 42 as an integer.
        Convert.ToInt64(result).Should().Be(42);
    }
}

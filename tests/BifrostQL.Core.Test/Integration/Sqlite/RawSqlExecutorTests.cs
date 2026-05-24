using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests for <see cref="RawSqlExecutor"/> against a real SQLite
/// database. Uses a temp file (not :memory:) because each
/// <c>SqliteDbConnFactory.GetConnection()</c> opens an independent connection,
/// and an in-memory database is scoped to a single connection — the executor
/// opens its own, so seed data written on another connection would be invisible.
/// </summary>
public sealed class RawSqlExecutorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public RawSqlExecutorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-rawsql-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private async Task SeedAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE widget (id INTEGER PRIMARY KEY, name TEXT, qty INTEGER)",
            null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO widget (id, name, qty) VALUES (1,'alpha',10),(2,'beta',20),(3,'gamma',NULL)",
            null, 30, 1000);
    }

    [Fact]
    public async Task Select_ReturnsColumnsInOrderAndPositionalRows()
    {
        await SeedAsync();

        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT id, name, qty FROM widget ORDER BY id", null, 30, 1000);

        result.Columns.Select(c => c.Name).Should().Equal("id", "name", "qty");
        result.Rows.Should().HaveCount(3);
        result.Rows[0].Should().Equal(1L, "alpha", 10L);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Select_NullCell_IsNull()
    {
        await SeedAsync();

        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT qty FROM widget WHERE id = 3", null, 30, 1000);

        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Should().BeNull();
    }

    [Fact]
    public async Task Select_RespectsMaxRowsAndSetsTruncated()
    {
        await SeedAsync();

        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT id FROM widget ORDER BY id", null, 30, maxRows: 2);

        result.Rows.Should().HaveCount(2);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Select_AtExactMaxRows_NotTruncated()
    {
        await SeedAsync();

        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT id FROM widget ORDER BY id", null, 30, maxRows: 3);

        result.Rows.Should().HaveCount(3);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Parameters_AreBound()
    {
        await SeedAsync();

        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT name FROM widget WHERE qty = @q",
            new Dictionary<string, object?> { ["q"] = 20 }, 30, 1000);

        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Should().Be("beta");
    }

    [Fact]
    public async Task Update_ReturnsRowsAffectedAndNoColumns()
    {
        await SeedAsync();

        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "UPDATE widget SET qty = 99 WHERE qty IS NULL", null, 30, 1000);

        result.Columns.Should().BeEmpty();
        result.Rows.Should().BeEmpty();
        result.RowsAffected.Should().Be(1);
    }

    [Fact]
    public async Task DuplicateColumnNames_BothPreservedPositionally()
    {
        await SeedAsync();

        // Name-keyed shapes would collapse these; positional rows keep both.
        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT id AS x, name AS x FROM widget WHERE id = 1", null, 30, 1000);

        result.Columns.Should().HaveCount(2);
        result.Rows[0].Should().HaveCount(2);
        result.Rows[0][0].Should().Be(1L);
        result.Rows[0][1].Should().Be("alpha");
    }

    [Fact]
    public async Task InvalidSql_ThrowsDbException()
    {
        var act = async () => await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT * FROM nonexistent_table", null, 30, 1000);

        await act.Should().ThrowAsync<System.Data.Common.DbException>();
    }
}

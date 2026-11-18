using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests for SqliteDbConnFactory verifying it produces
/// working connections, correct dialect, schema reader, and type mapper.
/// </summary>
public sealed class SqliteDbConnFactoryTests
{
    [Fact]
    public void Dialect_IsSqliteDialect()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.Dialect.Should().BeOfType<SqliteDialect>();
        factory.Dialect.Should().BeSameAs(SqliteDialect.Instance);
    }

    [Fact]
    public void SchemaReader_IsSqliteSchemaReader()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.SchemaReader.Should().BeOfType<SqliteSchemaReader>();
    }

    [Fact]
    public void TypeMapper_IsSqliteTypeMapper()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.TypeMapper.Should().BeOfType<SqliteTypeMapper>();
        factory.TypeMapper.Should().BeSameAs(SqliteTypeMapper.Instance);
    }

    [Fact]
    public void GetConnection_ReturnsWorkingConnection()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        using var conn = factory.GetConnection();

        conn.Should().BeOfType<SqliteConnection>();
    }

    [Fact]
    public async Task GetConnection_CanOpenAndQuery()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        await using var conn = factory.GetConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = await cmd.ExecuteScalarAsync();
        ((long)result!).Should().Be(1);
    }

    /// <summary>
    /// The busy wait below is the driver's, bounded by <c>Default Timeout</c>, so the factory's
    /// part is to leave that bound alone: a value the connection string sets reaches the
    /// connection unchanged, and a connection string that sets none gets the driver's 30 s.
    /// Two connection strings that differ only in the timeout, so a forced value fails one.
    /// </summary>
    [Theory]
    [InlineData("Data Source=:memory:;Default Timeout=5", 5)]
    [InlineData("Data Source=:memory:;Default Timeout=45", 45)]
    [InlineData("Data Source=:memory:", 30)]
    public void GetConnection_LeavesTheBusyTimeoutToTheConnectionString(string connectionString, int expectedSeconds)
    {
        using var conn = (SqliteConnection)new SqliteDbConnFactory(connectionString).GetConnection();

        conn.DefaultTimeout.Should().Be(expectedSeconds);
    }

    /// <summary>
    /// Two connections writing at once: the second waits for the first's write lock instead of
    /// failing with SQLITE_BUSY. The factory sets no busy timeout of its own; this holds because
    /// Microsoft.Data.Sqlite retries a busy statement until the connection's <c>Default Timeout</c>
    /// (30 s unless the connection string sets one), which the factory leaves as given.
    /// </summary>
    [Fact]
    public async Task A_writer_waits_for_a_concurrent_write_transaction_instead_of_failing_busy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bifrost-sqlite-busy-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteDbConnFactory($"Data Source={path};Pooling=False");
            await using (var setup = factory.GetConnection())
            {
                await setup.OpenAsync();
                await using var create = setup.CreateCommand();
                create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)";
                await create.ExecuteNonQueryAsync();
            }

            await using var holder = factory.GetConnection();
            await holder.OpenAsync();
            await using var tx = await holder.BeginTransactionAsync();
            await using (var first = holder.CreateCommand())
            {
                first.Transaction = tx;
                first.CommandText = "INSERT INTO t (id, v) VALUES (1, 'holder')";
                await first.ExecuteNonQueryAsync();
            }

            // The second writer starts while the first holds the write lock.
            var second = Task.Run(async () =>
            {
                await using var writer = factory.GetConnection();
                await writer.OpenAsync();
                await using var insert = writer.CreateCommand();
                insert.CommandText = "INSERT INTO t (id, v) VALUES (2, 'waiter')";
                return await insert.ExecuteNonQueryAsync();
            });
            await Task.Delay(200);
            if (second.IsFaulted)
                await second; // surfaces SQLITE_BUSY ("database is locked") as the failure
            second.IsCompleted.Should().BeFalse("the second writer is blocked behind the first one's lock");
            await tx.CommitAsync();

            (await second).Should().Be(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_ThrowsOnNullConnectionString()
    {
        var act = () => new SqliteDbConnFactory(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyConnectionString()
    {
        var act = () => new SqliteDbConnFactory("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnWhitespaceConnectionString()
    {
        var act = () => new SqliteDbConnFactory("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dialect_ImplementsISqlDialect()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.Dialect.Should().BeAssignableTo<ISqlDialect>();
    }

    [Fact]
    public void SchemaReader_ImplementsISchemaReader()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.SchemaReader.Should().BeAssignableTo<ISchemaReader>();
    }

    [Fact]
    public void TypeMapper_ImplementsITypeMapper()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.TypeMapper.Should().BeAssignableTo<ITypeMapper>();
    }

    [Fact]
    public void Factory_ImplementsIDbConnFactory()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        factory.Should().BeAssignableTo<IDbConnFactory>();
    }

    [Fact]
    public async Task SchemaReader_CanReadSchemaFromConnection()
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");
        await using var conn = factory.GetConnection();
        await conn.OpenAsync();

        // Create a table
        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)";
        await createCmd.ExecuteNonQueryAsync();

        var schema = await factory.SchemaReader.ReadSchemaAsync(conn);
        schema.Tables.Should().ContainSingle(t => t.DbName == "Test");
    }
}

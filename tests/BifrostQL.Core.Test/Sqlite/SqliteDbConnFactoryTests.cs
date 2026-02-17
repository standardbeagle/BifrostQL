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

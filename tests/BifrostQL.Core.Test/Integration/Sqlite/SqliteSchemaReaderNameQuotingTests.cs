using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Table and index names come out of <c>sqlite_master</c> and go back into the per-table
/// pragmas. A name that is a keyword, has a space, or carries SQL must reach the pragma as
/// a value: the desktop UI opens database files the user did not write, so a crafted table
/// name must never run as a statement while the schema is read.
/// </summary>
public sealed class SqliteSchemaReaderNameQuotingTests
{
    private const string InjectingTable = "t) ; DELETE FROM victims; --";

    private readonly SqliteSchemaReader _reader = new();

    private static async Task<SqliteConnection> OpenAsync(params string[] ddl)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        foreach (var sql in ddl)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        return connection;
    }

    private static Task<SqliteConnection> OpenInjectingAsync() => OpenAsync(
        "CREATE TABLE victims (id INTEGER PRIMARY KEY, name TEXT)",
        "INSERT INTO victims (id, name) VALUES (1, 'a'), (2, 'b')",
        $"CREATE TABLE \"{InjectingTable}\" (id INTEGER PRIMARY KEY, label TEXT)");

    [Fact]
    public async Task ReadSchema_TableNamedWithSql_NeverRunsIt()
    {
        await using var connection = await OpenInjectingAsync();

        await _reader.ReadSchemaAsync(connection);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM victims";
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(2);
    }

    [Fact]
    public async Task ReadSchema_TableNamedWithSql_ReadsItsColumns()
    {
        await using var connection = await OpenInjectingAsync();

        var schema = await _reader.ReadSchemaAsync(connection);

        schema.RawColumns.Where(c => c.TableName == InjectingTable).Select(c => c.ColumnName)
            .Should().Equal("id", "label");
    }

    [Fact]
    public async Task ReadSchema_KeywordAndSpacedNames_ReadColumnsKeysAndIndexes()
    {
        await using var connection = await OpenAsync(
            "CREATE TABLE \"order\" (id INTEGER PRIMARY KEY, code TEXT NOT NULL)",
            "CREATE UNIQUE INDEX \"order code\" ON \"order\" (code)",
            "CREATE TABLE \"order details\" (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES \"order\" (id), qty INTEGER)",
            "CREATE INDEX \"by order\" ON \"order details\" (order_id)",
            "CREATE UNIQUE INDEX \"by lower\" ON \"order details\" (lower(qty))");

        var schema = await _reader.ReadSchemaAsync(connection);

        schema.RawColumns.Where(c => c.TableName == "order").Select(c => c.ColumnName)
            .Should().Equal("id", "code");
        schema.RawColumns.Single(c => c.TableName == "order" && c.ColumnName == "code").IsUnique.Should().BeTrue();
        schema.RawColumns.Where(c => c.TableName == "order details").Select(c => c.ColumnName)
            .Should().Equal("id", "order_id", "qty");

        var fk = schema.ForeignKeys.Should().ContainSingle(f => f.ChildTableName == "order details").Subject;
        fk.ParentTableName.Should().Be("order");
        fk.ChildColumnNames.Should().Equal("order_id");

        schema.Indexes.Should().Contain(i => i.TableName == "order details" && i.Name == "by order"
                                             && i.ColumnNames.SequenceEqual(new[] { "order_id" }));
        schema.Indexes.Should().Contain(i => i.TableName == "order" && i.Name == "order code" && i.IsUnique);
        // An expression key names no column a client could sort or match on.
        schema.Indexes.Should().NotContain(i => i.Name == "by lower");
    }
}

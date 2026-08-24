using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Verifies <see cref="TableDdlGenerator.Generate"/> against a real loaded SQLite
/// model: dialect-escaped identifiers (never hard-coded brackets), a TABLE-level
/// PRIMARY KEY so composite keys come out correct, NULL/NOT NULL from the column
/// facts, and length rendering for sized types. Temp file (not :memory:) so the
/// schema reader sees the tables created on a prior connection.
/// </summary>
public sealed class TableDdlGeneratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public TableDdlGeneratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-ddl-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private async Task<IDbModel> SeedAndLoadAsync(params string[] ddl)
    {
        await using (var conn = _factory.GetConnection())
        {
            await conn.OpenAsync();
            foreach (var statement in ddl)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        return await new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    [Fact]
    public async Task Generate_CompositeKey_EmitsTableLevelPrimaryKey()
    {
        var model = await SeedAndLoadAsync(
            "CREATE TABLE enrollment (student_id INTEGER NOT NULL, course_id TEXT NOT NULL, grade TEXT NULL, PRIMARY KEY (student_id, course_id))");
        var table = model.GetTableFromDbName("enrollment");

        var ddl = TableDdlGenerator.Generate(table, _factory.Dialect);

        ddl.Should().StartWith("CREATE TABLE ");
        // Dialect escaping — SQLite quotes with double quotes, never brackets.
        ddl.Should().Contain("\"enrollment\"");
        ddl.Should().NotContain("[enrollment]");
        ddl.Should().Contain("PRIMARY KEY (\"student_id\", \"course_id\")",
            "a composite key must be one table-level constraint, not per-column markers");
        ddl.Should().Contain("\"grade\"");
        ddl.Should().Contain("NULL");
        ddl.Should().Contain("\"student_id\" INTEGER NOT NULL");
        ddl.Should().EndWith(");");
    }

    [Fact]
    public async Task Generate_SizedTypes_RenderLength()
    {
        var model = await SeedAndLoadAsync(
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, title VARCHAR(80) NOT NULL, body TEXT NULL)");
        var table = model.GetTableFromDbName("notes");

        var ddl = TableDdlGenerator.Generate(table, _factory.Dialect);

        // SQLite reports the declared type; a sized declaration keeps its size.
        ddl.Should().MatchRegex(@"""title""\s+VARCHAR\(80\)\s+NOT NULL");
        ddl.Should().Contain("PRIMARY KEY (\"id\")");
    }
}

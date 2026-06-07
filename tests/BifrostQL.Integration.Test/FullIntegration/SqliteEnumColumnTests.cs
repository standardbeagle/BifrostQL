using BifrostQL.Sqlite;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// End-to-end lookup-table enum coverage on SQLite (in-memory). Always runs.
/// </summary>
[Collection("SqliteEnumColumn")]
public class SqliteEnumColumnTests : EnumColumnIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private SqliteConnection? _keepAliveConnection;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_enum_full_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        await _keepAliveConnection.OpenAsync();

        var factory = new SqliteDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync, EnumMetadataRules);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_keepAliveConnection != null)
            await _keepAliveConnection.DisposeAsync();
    }

    // Lookup values are inserted here (before the model/enum snapshot loads).
    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "CREATE TABLE status (id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL UNIQUE)",
            "INSERT INTO status (code) VALUES ('active'), ('inactive')",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NULL REFERENCES status(code))",
        };
        foreach (var statement in statements)
        {
            var cmd = new SqliteCommand(statement, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var cmd = new SqliteCommand(
            "INSERT INTO orders (status) VALUES ('active'), ('active'), ('inactive')",
            (SqliteConnection)conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

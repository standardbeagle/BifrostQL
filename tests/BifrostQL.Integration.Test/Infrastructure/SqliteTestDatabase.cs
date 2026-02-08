using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Sqlite;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// SQLite file-based test database. Always available, no Docker required.
/// Each instance creates a unique temp file that is deleted on disposal.
/// </summary>
public sealed class SqliteTestDatabase : IIntegrationTestDatabase
{
    private string _dbPath = null!;

    public IDbConnFactory ConnFactory { get; private set; } = null!;
    public ISqlDialect Dialect => SqliteDialect.Instance;
    public IDbModel DbModel { get; private set; } = null!;
    public string ProviderName => "SQLite";

    public async ValueTask InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost_test_{Guid.NewGuid():N}.db");
        var connString = $"Data Source={_dbPath}";
        ConnFactory = new SqliteDbConnFactory(connString);
        DbModel = TestSchema.BuildDbModel();

        using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();

        // Execute DDL
        var ddl = TestSchema.GetCreateTablesSql(Dialect);
        var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();

        // Seed data
        await TestSchema.SeedDataAsync(conn, Dialect);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch
        {
            // Best effort cleanup
        }
        return ValueTask.CompletedTask;
    }
}

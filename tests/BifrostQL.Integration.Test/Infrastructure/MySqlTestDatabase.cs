using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.MySql;
using MySqlConnector;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// MySQL test database. Requires BIFROST_TEST_MYSQL env var with connection string.
/// Creates a unique database per test class; drops it on disposal.
/// </summary>
public sealed class MySqlTestDatabase : IIntegrationTestDatabase
{
    private string? _testDbName;
    private string? _masterConnString;

    public IDbConnFactory ConnFactory { get; private set; } = null!;
    public ISqlDialect Dialect => MySqlDialect.Instance;
    public IDbModel DbModel { get; private set; } = null!;
    public string ProviderName => "MySQL";

    public static string? ConnectionString => Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");

    public async ValueTask InitializeAsync()
    {
        var connString = ConnectionString
            ?? throw new InvalidOperationException("BIFROST_TEST_MYSQL environment variable not set");

        _masterConnString = connString;
        _testDbName = $"bifrost_test_{Guid.NewGuid():N}";

        // Create the test database
        await using var masterConn = new MySqlConnection(connString);
        await masterConn.OpenAsync();
        var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE `{_testDbName}`";
        await createCmd.ExecuteNonQueryAsync();

        // Build conn string for test database
        var builder = new MySqlConnectionStringBuilder(connString)
        {
            Database = _testDbName
        };
        ConnFactory = new MySqlDbConnFactory(builder.ConnectionString);
        DbModel = TestSchema.BuildDbModel();

        using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();

        // MySQL needs statements executed individually
        var ddl = TestSchema.GetCreateTablesSql(Dialect);
        foreach (var statement in ddl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }

        await TestSchema.SeedDataAsync(conn, Dialect);
    }

    public async ValueTask DisposeAsync()
    {
        if (_testDbName == null || _masterConnString == null) return;

        try
        {
            await using var conn = new MySqlConnection(_masterConnString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{_testDbName}`";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

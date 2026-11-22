using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.MySql;
using MySqlConnector;
using Xunit;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// End-to-end lookup-table enum coverage on MySQL.
/// Only runs when BIFROST_TEST_MYSQL is set.
/// </summary>
[Collection("MySqlEnumColumn")]
public class MySqlEnumColumnTests : EnumColumnIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_MYSQL environment variable not set");
            return;
        }

        _testDbName = $"bifrost_enum_int_{Guid.NewGuid():N}";

        await using var masterConn = new MySqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new MySqlCommand($"CREATE DATABASE `{_testDbName}`", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new MySqlConnectionStringBuilder(masterConnString) { Database = _testDbName };
        _connectionString = builder.ConnectionString;

        try
        {
            var factory = new MySqlDbConnFactory(_connectionString);
            await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync, EnumMetadataRules);
        }
        catch (Exception setupFailure)
        {
            // Drop here: DisposeAsync does not run after a failed InitializeAsync. The setup
            // failure is what the test reports, never a cleanup error.
            var name = _testDbName!;
            _testDbName = null;
            await TestDatabaseDrop.RethrowAfterDropAsync(setupFailure, async () =>
            {
                await base.CleanupAsync();
                await TestDatabaseDrop.MySqlAsync(masterConnString, name);
            });
        }
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;

        await TestDatabaseDrop.MySqlAsync(Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL")!, _testDbName);
    }

    // Lowercase identifiers: MySQL on Linux is case-sensitive by default.
    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            @"CREATE TABLE status (
                id INT AUTO_INCREMENT PRIMARY KEY,
                code VARCHAR(50) NOT NULL UNIQUE
            )",
            "INSERT INTO status (code) VALUES ('active'), ('inactive')",
            @"CREATE TABLE orders (
                id INT AUTO_INCREMENT PRIMARY KEY,
                status VARCHAR(50) NULL,
                CONSTRAINT fk_orders_status FOREIGN KEY (status) REFERENCES status(code)
            )",
        };
        foreach (var statement in statements)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO orders (status) VALUES ('active'), ('active'), ('inactive')";
        await cmd.ExecuteNonQueryAsync();
    }
}

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

        var factory = new MySqlDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync, EnumMetadataRules);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
            if (masterConnString == null) return;

            await using var conn = new MySqlConnection(masterConnString);
            await conn.OpenAsync();
            var dropCmd = new MySqlCommand($"DROP DATABASE IF EXISTS `{_testDbName}`", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch { }
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

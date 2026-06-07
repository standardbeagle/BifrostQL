using BifrostQL.Ngsql;
using Npgsql;
using Xunit;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// End-to-end lookup-table enum coverage on PostgreSQL.
/// Only runs when BIFROST_TEST_POSTGRES is set.
/// </summary>
[Collection("PostgresEnumColumn")]
public class PostgresEnumColumnTests : EnumColumnIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_POSTGRES environment variable not set");
            return;
        }

        _testDbName = $"bifrost_enum_int_{Guid.NewGuid():N}";

        await using var masterConn = new NpgsqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new NpgsqlCommand($"CREATE DATABASE {_testDbName}", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(masterConnString) { Database = _testDbName };
        _connectionString = builder.ConnectionString;

        var factory = new PostgresDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync, EnumMetadataRules);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
            if (masterConnString == null) return;

            await using var conn = new NpgsqlConnection(masterConnString);
            await conn.OpenAsync();

            var terminateCmd = new NpgsqlCommand($@"
                SELECT pg_terminate_backend(pg_stat_activity.pid)
                FROM pg_stat_activity
                WHERE pg_stat_activity.datname = '{_testDbName}'
                AND pid <> pg_backend_pid()", conn);
            await terminateCmd.ExecuteNonQueryAsync();

            var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_testDbName}", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var ddl = @"
CREATE TABLE status (
    id SERIAL PRIMARY KEY,
    code VARCHAR(50) NOT NULL UNIQUE
);
INSERT INTO status (code) VALUES ('active'), ('inactive');

CREATE TABLE orders (
    id SERIAL PRIMARY KEY,
    status VARCHAR(50) NULL,
    CONSTRAINT fk_orders_status FOREIGN KEY (status) REFERENCES status(code)
);
";
        var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO orders (status) VALUES ('active'), ('active'), ('inactive');";
        await cmd.ExecuteNonQueryAsync();
    }
}

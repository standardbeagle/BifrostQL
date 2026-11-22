using BifrostQL.Integration.Test.Infrastructure;
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

        try
        {
            var factory = new PostgresDbConnFactory(_connectionString);
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
                await TestDatabaseDrop.PostgresAsync(masterConnString, name);
            });
        }
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;

        await TestDatabaseDrop.PostgresAsync(Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES")!, _testDbName);
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

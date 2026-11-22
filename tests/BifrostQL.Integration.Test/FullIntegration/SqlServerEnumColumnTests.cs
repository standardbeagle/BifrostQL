using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Core.Model;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// End-to-end lookup-table enum coverage on SQL Server.
/// Only runs when BIFROST_TEST_SQLSERVER is set.
/// </summary>
[Collection("SqlServerEnumColumn")]
public class SqlServerEnumColumnTests : EnumColumnIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;

    internal string? TestDatabaseName => _testDbName;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_SQLSERVER environment variable not set");
            return;
        }

        _testDbName = $"BifrostEnumInt_{Guid.NewGuid():N}";

        await using var masterConn = new SqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new SqlCommand($"CREATE DATABASE [{_testDbName}]", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new SqlConnectionStringBuilder(masterConnString) { InitialCatalog = _testDbName };
        _connectionString = builder.ConnectionString;

        try
        {
            var factory = new SqlServerDbConnFactory(_connectionString);
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
                await TestDatabaseDrop.SqlServerAsync(masterConnString, name);
            });
        }
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;

        await TestDatabaseDrop.SqlServerAsync(Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER")!, _testDbName);
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var ddl = @"
CREATE TABLE status (
    id INT IDENTITY(1,1) PRIMARY KEY,
    code NVARCHAR(50) NOT NULL UNIQUE
);
INSERT INTO status (code) VALUES ('active'), ('inactive');

CREATE TABLE orders (
    id INT IDENTITY(1,1) PRIMARY KEY,
    status NVARCHAR(50) NULL,
    CONSTRAINT FK_Orders_Status FOREIGN KEY (status) REFERENCES status(code)
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

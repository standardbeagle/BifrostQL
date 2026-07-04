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

        var factory = new SqlServerDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync, EnumMetadataRules);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER")
                ?? "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";
            await using var conn = new SqlConnection(masterConnString);
            await conn.OpenAsync();
            var dropCmd = new SqlCommand($"DROP DATABASE IF EXISTS [{_testDbName}]", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch { }
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

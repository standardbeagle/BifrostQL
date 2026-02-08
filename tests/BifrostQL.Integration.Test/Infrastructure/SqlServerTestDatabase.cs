using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// SQL Server test database. Requires BIFROST_TEST_SQLSERVER env var with connection string.
/// Creates a unique database per test class; drops it on disposal.
/// </summary>
public sealed class SqlServerTestDatabase : IIntegrationTestDatabase
{
    private string? _testDbName;
    private string? _masterConnString;

    public IDbConnFactory ConnFactory { get; private set; } = null!;
    public ISqlDialect Dialect => SqlServerDialect.Instance;
    public IDbModel DbModel { get; private set; } = null!;
    public string ProviderName => "SQL Server";

    public static string? ConnectionString => Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");

    public async ValueTask InitializeAsync()
    {
        var connString = ConnectionString
            ?? throw new InvalidOperationException("BIFROST_TEST_SQLSERVER environment variable not set");

        _masterConnString = connString;
        _testDbName = $"bifrost_test_{Guid.NewGuid():N}";

        // Create the test database
        await using var masterConn = new SqlConnection(connString);
        await masterConn.OpenAsync();
        var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE [{_testDbName}]";
        await createCmd.ExecuteNonQueryAsync();

        // Build conn string for test database
        var builder = new SqlConnectionStringBuilder(connString)
        {
            InitialCatalog = _testDbName
        };
        ConnFactory = new DbConnFactory(builder.ConnectionString);
        DbModel = TestSchema.BuildDbModel();

        using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();

        var ddl = TestSchema.GetCreateTablesSql(Dialect);
        var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();

        await TestSchema.SeedDataAsync(conn, Dialect);
    }

    public async ValueTask DisposeAsync()
    {
        if (_testDbName == null || _masterConnString == null) return;

        try
        {
            await using var conn = new SqlConnection(_masterConnString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                ALTER DATABASE [{_testDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_testDbName}];
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup
        }
        GC.SuppressFinalize(this);
    }
}

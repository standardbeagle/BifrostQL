using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Ngsql;
using Npgsql;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// PostgreSQL test database. Requires BIFROST_TEST_POSTGRES env var with connection string.
/// Creates a unique database per test class; drops it on disposal.
/// </summary>
public sealed class PostgresTestDatabase : IIntegrationTestDatabase
{
    private string? _testDbName;
    private string? _masterConnString;

    public IDbConnFactory ConnFactory { get; private set; } = null!;
    public ISqlDialect Dialect => PostgresDialect.Instance;
    public IDbModel DbModel { get; private set; } = null!;
    public string ProviderName => "PostgreSQL";

    public static string? ConnectionString => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    public async ValueTask InitializeAsync()
    {
        var connString = ConnectionString
            ?? throw new InvalidOperationException("BIFROST_TEST_POSTGRES environment variable not set");

        _masterConnString = connString;
        _testDbName = $"bifrost_test_{Guid.NewGuid():N}";

        // Create the test database
        await using var masterConn = new NpgsqlConnection(connString);
        await masterConn.OpenAsync();
        var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{_testDbName}\"";
        await createCmd.ExecuteNonQueryAsync();

        try
        {
            // Build conn string for test database
            var builder = new NpgsqlConnectionStringBuilder(connString)
            {
                Database = _testDbName
            };
            ConnFactory = new PostgresDbConnFactory(builder.ConnectionString);
            DbModel = TestSchema.BuildDbModel();

            using var conn = ConnFactory.GetConnection();
            await conn.OpenAsync();

            var ddl = TestSchema.GetCreateTablesSql(Dialect);
            var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync();

            await TestSchema.SeedDataAsync(conn, Dialect);
        }
        catch (Exception setupFailure)
        {
            // Drop here: DisposeAsync does not run after a failed InitializeAsync. The setup
            // failure is what the test reports, never a cleanup error.
            var name = _testDbName!;
            _testDbName = null;
            await TestDatabaseDrop.RethrowAfterDropAsync(setupFailure, async () =>
            {
                await TestDatabaseDrop.PostgresAsync(connString, name);
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_testDbName == null || _masterConnString == null) return;

        await TestDatabaseDrop.PostgresAsync(_masterConnString, _testDbName);
    }
}

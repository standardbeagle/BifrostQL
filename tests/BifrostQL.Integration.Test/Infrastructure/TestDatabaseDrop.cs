using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// The one way an integration test drops the database it created, per engine. Every drop
/// throws on failure: a swallowed drop leaks one database per test and hides the cause.
/// </summary>
public static class TestDatabaseDrop
{
    /// <summary>
    /// Pooled connections keep the database in use, so a bare DROP fails with error 3702.
    /// Clearing the pools and forcing SINGLE_USER WITH ROLLBACK IMMEDIATE ends every session
    /// first; DB_ID makes the drop a no-op when the database was never created.
    /// </summary>
    public static async Task SqlServerAsync(string masterConnectionString, string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(masterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Pooled connections keep the database in use; WITH (FORCE) terminates every session
    /// in the same statement, so no connection can reattach between terminate and drop.
    /// </summary>
    public static async Task PostgresAsync(string masterConnectionString, string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(masterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>MySQL drops a database with open sessions, so a plain DROP is enough.</summary>
    public static async Task MySqlAsync(string masterConnectionString, string databaseName)
    {
        await using var conn = new MySqlConnection(masterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="drop"/> after a setup or test-body failure and rethrows that
    /// failure, so a cleanup error never replaces it. When the drop fails too, both surface
    /// together. Use it from a catch, never a finally: a finally drop that throws replaces the
    /// test's own failure.
    /// </summary>
    public static async Task RethrowAfterDropAsync(Exception failure, Func<Task> drop)
    {
        try
        {
            await drop();
        }
        catch (Exception dropFailure)
        {
            throw new AggregateException("The test failed, and dropping its database failed too.", failure, dropFailure);
        }
        ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

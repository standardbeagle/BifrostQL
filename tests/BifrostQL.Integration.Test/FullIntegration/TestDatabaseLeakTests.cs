using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// A test class that creates a database must drop it. SqlServerEnumColumnTests ran a bare
/// DROP inside <c>catch { }</c>; its pooled connections held the database, the drop failed
/// with error 3702, and the failure was swallowed, leaking one database per test.
/// </summary>
[Collection("SqlServerEnumColumn")]
public sealed class TestDatabaseLeakTests
{
    [SkippableFact]
    public async Task SqlServerEnumColumnTests_DropsItsDatabase()
    {
        var master = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(master == null, "BIFROST_TEST_SQLSERVER environment variable not set");

        var fixture = new SqlServerEnumColumnTests();
        await fixture.InitializeAsync();
        var name = fixture.TestDatabaseName!;
        // A query leaves a pooled connection on the database, the state the drop must survive.
        await fixture.Read_MapsStoredValueToEnumName();
        (await CountDatabasesAsync(master!, name)).Should().Be(1, "the fixture created it");

        await fixture.DisposeAsync();

        (await CountDatabasesAsync(master!, name)).Should().Be(0, "DisposeAsync must drop the database it created");
    }

    private static async Task<int> CountDatabasesAsync(string master, string name)
    {
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}

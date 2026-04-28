using BifrostQL.Core.Model;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace BifrostQL.Integration.Test.DatabaseEnumeration;

/// <summary>
/// Integration tests for IDbConnFactory.ListDatabasesAsync().
/// Tests against live PostgreSQL and MySQL Docker containers.
/// Skips gracefully when containers are not available.
/// </summary>
public sealed class PostgresListDatabasesTests
{
    private static string? ConnString => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    [SkippableFact]
    public async Task ListDatabasesAsync_ReturnsNonEmptyList()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_POSTGRES not set");
        var factory = new PostgresDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().NotBeEmpty("a PostgreSQL server always has at least the 'postgres' database");
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_ContainsPostgresSystemDb()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_POSTGRES not set");
        var factory = new PostgresDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().Contain("postgres");
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_ExcludesTemplates()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_POSTGRES not set");
        var factory = new PostgresDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().NotContain("template0");
        databases.Should().NotContain("template1");
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_ResultIsSorted()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_POSTGRES not set");
        var factory = new PostgresDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().BeInAscendingOrder();
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_CreatedDbAppearsInList()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_POSTGRES not set");
        var factory = new PostgresDbConnFactory(ConnString!);
        var testDbName = $"bifrost_enum_test_{Guid.NewGuid():N}";

        try
        {
            await using var conn = new NpgsqlConnection(ConnString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{testDbName}\"";
            await cmd.ExecuteNonQueryAsync();

            var databases = await factory.ListDatabasesAsync();
            databases.Should().Contain(testDbName);
        }
        finally
        {
            await using var conn = new NpgsqlConnection(ConnString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{testDbName}';
                DROP DATABASE IF EXISTS "{testDbName}";
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

public sealed class MySqlListDatabasesTests
{
    private static string? ConnString => Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");

    [SkippableFact]
    public async Task ListDatabasesAsync_ReturnsNonEmptyList()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_MYSQL not set");
        var factory = new MySqlDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().NotBeEmpty("a MySQL server always has system schemas");
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_ContainsInformationSchema()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_MYSQL not set");
        var factory = new MySqlDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().Contain("information_schema");
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_ResultIsSorted()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_MYSQL not set");
        var factory = new MySqlDbConnFactory(ConnString!);
        var databases = await factory.ListDatabasesAsync();
        databases.Should().BeInAscendingOrder();
    }

    [SkippableFact]
    public async Task ListDatabasesAsync_CreatedDbAppearsInList()
    {
        Skip.If(string.IsNullOrEmpty(ConnString), "BIFROST_TEST_MYSQL not set");
        var factory = new MySqlDbConnFactory(ConnString!);
        var testDbName = $"bifrost_enum_test_{Guid.NewGuid():N}";

        try
        {
            await using var conn = new MySqlConnection(ConnString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE `{testDbName}`";
            await cmd.ExecuteNonQueryAsync();

            var databases = await factory.ListDatabasesAsync();
            databases.Should().Contain(testDbName);
        }
        finally
        {
            await using var conn = new MySqlConnection(ConnString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{testDbName}`";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

public sealed class SqliteListDatabasesTests
{
    [Fact]
    public async Task ListDatabasesAsync_ReturnsEmptyArray()
    {
        IDbConnFactory factory = new SqliteDbConnFactory("Data Source=:memory:");
        var databases = await factory.ListDatabasesAsync();
        databases.Should().BeEmpty("SQLite does not support database enumeration");
    }
}

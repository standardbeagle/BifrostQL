using BifrostQL.Ngsql;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using Npgsql;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Real-Postgres roundtrip coverage for provider-specific CLR values that need
/// explicit text projection before GraphQL serialization.
/// </summary>
[Collection("PostgresTypeRoundtrip")]
public sealed class PostgresTypeRoundtripTests : FullIntegrationTestBase, IAsyncLifetime
{
    private string? _testDbName;
    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
        if (masterConnString == null)
        {
            await InitializeContainerAsync();
            return;
        }

        _testDbName = $"bifrost_type_roundtrip_{Guid.NewGuid():N}";

        await using var masterConn = new NpgsqlConnection(masterConnString);
        await masterConn.OpenAsync();
        await using (var createCmd = new NpgsqlCommand($"CREATE DATABASE {_testDbName}", masterConn))
            await createCmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(masterConnString) { Database = _testDbName };
        var factory = new PostgresDbConnFactory(builder.ConnectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    private async Task InitializeContainerAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();

            var factory = new PostgresDbConnFactory(_container.GetConnectionString());
            await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
        }
        catch (Exception ex)
        {
            if (_container != null)
                await _container.DisposeAsync();

            Skip.If(true, $"Docker/Testcontainers PostgreSQL unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_container != null)
        {
            await _container.DisposeAsync();
            return;
        }

        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
            if (masterConnString == null) return;

            await using var conn = new NpgsqlConnection(masterConnString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{_testDbName}' AND pid <> pg_backend_pid();
                DROP DATABASE IF EXISTS ""{_testDbName}"";
                ", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        const string ddl = """
CREATE TYPE mood AS ENUM ('happy', 'focused');

CREATE TABLE type_roundtrip (
    id SERIAL PRIMARY KEY,
    date_value DATE NOT NULL,
    time_value TIME NOT NULL,
    timetz_value TIME WITH TIME ZONE NOT NULL,
    timestamp_value TIMESTAMP NOT NULL,
    timestamptz_value TIMESTAMP WITH TIME ZONE NOT NULL,
    interval_value INTERVAL NOT NULL,
    inet_value INET NOT NULL,
    uuid_value UUID NOT NULL,
    jsonb_value JSONB NOT NULL,
    numeric_value NUMERIC(10,2) NOT NULL,
    enum_value mood NOT NULL
);
""";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        const string seed = """
INSERT INTO type_roundtrip (
    date_value,
    time_value,
    timetz_value,
    timestamp_value,
    timestamptz_value,
    interval_value,
    inet_value,
    uuid_value,
    jsonb_value,
    numeric_value,
    enum_value
) VALUES (
    DATE '2026-06-07',
    TIME '14:15:16',
    TIME WITH TIME ZONE '14:15:16+00',
    TIMESTAMP '2026-06-07 14:00:00',
    TIMESTAMP WITH TIME ZONE '2026-06-07 14:00:00+00',
    INTERVAL '1 day 02:03:04',
    INET '192.168.10.20',
    UUID '11111111-2222-3333-4444-555555555555',
    '{"ok": true, "count": 2}'::jsonb,
    123.45,
    'focused'
);
""";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = seed;
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Query_PostgresProviderTypes_RoundtripThroughGraphQl()
    {
        var result = await ExecuteQueryAsync("""
query {
  type_roundtrip {
    data {
      date_value
      time_value
      timetz_value
      timestamp_value
      timestamptz_value
      interval_value
      inet_value
      uuid_value
      jsonb_value
      numeric_value
      enum_value
    }
  }
}
""");

        var row = ExtractSingleRow(result, "type_roundtrip");
        row["date_value"].GetString().Should().Be("2026-06-07");
        row["time_value"].GetString().Should().StartWith("14:15:16");
        row["timetz_value"].GetString().Should().Contain("14:15:16");
        row["timestamp_value"].GetString().Should().Be("2026-06-07T14:00:00");
        row["timestamptz_value"].GetString().Should().Contain("T");
        row["interval_value"].GetString().Should().NotBeNullOrWhiteSpace();
        row["inet_value"].GetString().Should().Be("192.168.10.20");
        row["uuid_value"].GetString().Should().Be("11111111-2222-3333-4444-555555555555");
        row["jsonb_value"].ValueKind.Should().Be(JsonValueKind.Object);
        row["jsonb_value"].GetProperty("ok").GetBoolean().Should().BeTrue();
        row["numeric_value"].GetDecimal().Should().Be(123.45m);
        row["enum_value"].GetString().Should().Be("focused");
    }

    private static Dictionary<string, JsonElement> ExtractSingleRow(ExecutionResult result, string tableName)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        root.Should().ContainKey(tableName);
        var json = JsonSerializer.Serialize(root[tableName]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(paged["data"].GetRawText())!;
        rows.Should().ContainSingle();
        return rows[0];
    }
}

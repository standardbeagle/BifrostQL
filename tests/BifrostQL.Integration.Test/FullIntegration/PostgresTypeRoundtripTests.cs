using BifrostQL.Integration.Test.Infrastructure;
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

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(masterConnString) { Database = _testDbName };
            var factory = new PostgresDbConnFactory(builder.ConnectionString);
            await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
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

    private async Task InitializeContainerAsync()
    {
        try
        {
            // Labelled with the tman run that started it, so tman removes the container when that
            // run ends even if this process is killed before DisposeAsync. Outside tman the label
            // is empty and tman never touches it.
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithLabel("tman.run", Environment.GetEnvironmentVariable("TMAN_RUN_ID") ?? "")
                .Build();
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

        await TestDatabaseDrop.PostgresAsync(Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES")!, _testDbName);
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
    enum_value mood NOT NULL,
    wide_numeric NUMERIC(38,10) NOT NULL,
    wide_jsonb JSONB NOT NULL
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
    enum_value,
    wide_numeric,
    wide_jsonb
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
    'focused',
    123456789012345678.0123456789,
    '{"id": 12345678901234567890123, "rate": 0.12345678901234567890123, "small": 7}'::jsonb
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
        row["numeric_value"].GetString().Should().Be("123.45");
        row["enum_value"].GetString().Should().Be("focused");
    }

    /// <summary>
    /// A NUMERIC(38,10) value with 28 significant digits — far past the 15–17 a
    /// double keeps. Written as a JSON number it reaches every browser client
    /// through JSON.parse already rounded, so the Decimal scalar writes it as a
    /// decimal string, digits and scale intact.
    /// </summary>
    [SkippableFact]
    public async Task Read_WideDecimal_SerializesAsExactDecimalString()
    {
        var result = await ExecuteQueryAsync("query { type_roundtrip { data { wide_numeric } } }");

        var row = ExtractSingleRow(result, "type_roundtrip");
        row["wide_numeric"].ValueKind.Should().Be(JsonValueKind.String);
        row["wide_numeric"].GetString().Should().Be("123456789012345678.0123456789");
    }

    /// <summary>
    /// The write half: a decimal sent as text is stored exactly and read back as
    /// the same text. Through a double it would round on the way in.
    /// </summary>
    [SkippableFact]
    public async Task Write_WideDecimalAsText_RoundTripsExactly()
    {
        var write = await ExecuteQueryAsync(
            "mutation SetWide($id: Int!, $v: Decimal!) { type_roundtrip(update: {id: $id, wide_numeric: $v}) }",
            new Dictionary<string, object?> { ["id"] = 1, ["v"] = "-987654321098765432.9876543210" });
        write.Errors.Should().BeNullOrEmpty();

        var read = await ExecuteQueryAsync("query { type_roundtrip { data { wide_numeric } } }");
        ExtractSingleRow(read, "type_roundtrip")["wide_numeric"].GetString()
            .Should().Be("-987654321098765432.9876543210");
    }

    /// <summary>
    /// SUM/MAX over a decimal column are exact in SQL. The aggregate surface types
    /// a Decimal column's values as Decimal, so they stay exact on the wire instead
    /// of being squeezed through Float.
    /// </summary>
    [SkippableFact]
    public async Task Aggregate_WideDecimal_SerializesAsExactDecimalString()
    {
        var result = await ExecuteQueryAsync(
            "query { type_roundtripAggregate { _sum { wide_numeric } _max { wide_numeric } } }");

        result.Errors.Should().BeNullOrEmpty();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        var group = (Dictionary<string, object?>)((System.Collections.IList)root["type_roundtripAggregate"]!)[0]!;
        ((Dictionary<string, object?>)group["_sum"]!)["wide_numeric"].Should().Be("123456789012345678.0123456789");
        ((Dictionary<string, object?>)group["_max"]!)["wide_numeric"].Should().Be("123456789012345678.0123456789");
    }

    /// <summary>
    /// Numbers inside a native JSON column keep their exact text on the wire. jsonb
    /// stores them as numeric; parsing them to long/double on the way out rounded
    /// 12345678901234567890123 to 1.2345678901234568E+22 before any client saw it.
    /// Asserted on the serializer's output: the bytes a client actually receives.
    /// </summary>
    [SkippableFact]
    public async Task Read_JsonColumnNumbers_KeepExactTextOnTheWire()
    {
        var result = await ExecuteQueryAsync("query { type_roundtrip { data { wide_jsonb } } }");
        result.Errors.Should().BeNullOrEmpty();

        var json = WireRowField(result, "wide_jsonb");
        json.GetProperty("id").GetRawText().Should().Be("12345678901234567890123");
        json.GetProperty("rate").GetRawText().Should().Be("0.12345678901234567890123");
        json.GetProperty("small").GetRawText().Should().Be("7");
    }

    /// <summary>
    /// The JSON write path: an object sent as a variable (deserialized by the same
    /// serializer the HTTP front door uses) is stored with its numbers' exact text.
    /// </summary>
    [SkippableFact]
    public async Task Write_JsonColumnNumbers_StoreExactText()
    {
        var variables = new GraphQL.SystemTextJson.GraphQLSerializer().Deserialize<Inputs>(
            """{"id": 1, "v": {"id": 98765432109876543210987, "rate": 0.98765432109876543210987, "small": 7}}""")!;
        var write = await ExecuteQueryAsync(
            "mutation SetJson($id: Int!, $v: JSON!) { type_roundtrip(update: {id: $id, wide_jsonb: $v}) }",
            new Dictionary<string, object?>(variables));
        write.Errors.Should().BeNullOrEmpty();

        var json = WireRowField(await ExecuteQueryAsync("query { type_roundtrip { data { wide_jsonb } } }"), "wide_jsonb");
        json.GetProperty("id").GetRawText().Should().Be("98765432109876543210987");
        json.GetProperty("rate").GetRawText().Should().Be("0.98765432109876543210987");
        json.GetProperty("small").GetRawText().Should().Be("7");
    }

    private static JsonElement WireRowField(ExecutionResult result, string field)
    {
        result.Errors.Should().BeNullOrEmpty();
        var wire = new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(wire);
        return doc.RootElement.GetProperty("data").GetProperty("type_roundtrip")
            .GetProperty("data")[0].GetProperty(field).Clone();
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

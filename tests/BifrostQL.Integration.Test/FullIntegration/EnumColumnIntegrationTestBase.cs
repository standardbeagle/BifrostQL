using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using System.Text.Json;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Shared end-to-end coverage for lookup-table enums across every SQL dialect.
///
/// Scenario (DDL adapted per engine by the concrete subclass):
///   status(id PK auto, code UNIQUE)   seeded with 'active','inactive'
///   orders(id PK auto, status FK -> status(code))   rows referencing both
///
/// The metadata rule "*.status { enum: code }" marks the lookup table as an
/// enum whose value column is <c>code</c>; the referencing FK column
/// <c>orders.status</c> then resolves to the emitted enum type, so the API
/// exposes enum names (ACTIVE/INACTIVE) while the database keeps the raw
/// strings ('active'/'inactive').
///
/// Concrete subclasses only supply the engine-specific connection factory,
/// DDL/seed, and availability skip — these tests are inherited unchanged.
/// </summary>
public abstract class EnumColumnIntegrationTestBase : FullIntegrationTestBase
{
    // Selector "*" matches any schema (verified against the Core enum-loading
    // test). The value column is the lookup table's string column.
    protected static readonly string[] EnumMetadataRules = { "*.status { enum: code }" };

    [SkippableFact]
    public async Task Read_MapsStoredValueToEnumName()
    {
        var rows = QueryRows(await ExecuteQueryAsync("query { orders { data { id status } } }"), "orders");

        rows.Should().NotBeEmpty();
        // Stored values are 'active'/'inactive'; the API must surface the
        // GraphQL enum names, never the raw lowercase DB strings.
        var statuses = rows.Select(r => Str(r["status"])).ToList();
        statuses.Should().Contain("ACTIVE");
        statuses.Should().NotContain("active");
        statuses.Where(s => s != null).Should().OnlyContain(s => s == "ACTIVE" || s == "INACTIVE");
    }

    [SkippableFact]
    public async Task Filter_ByEnumName_ReturnsMatchingRows()
    {
        // Enum operands are unquoted GraphQL enum literals, not strings.
        var rows = QueryRows(
            await ExecuteQueryAsync("query { orders(filter: { status: { _eq: ACTIVE } }) { data { id status } } }"),
            "orders");

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => Str(r["status"]) == "ACTIVE");
    }

    [SkippableFact]
    public async Task Insert_ByEnumName_PersistsUnderlyingValue()
    {
        var insert = await ExecuteQueryAsync("mutation { orders(insert: { status: INACTIVE }) }");
        insert.Errors.Should().BeNullOrEmpty();
        var newId = MutationScalar(insert, "orders");

        // The write transformer must map the enum name back to the DB value:
        // the raw stored string is 'inactive', not 'INACTIVE'.
        var raw = await ReadRawStatusAsync(newId);
        raw.Should().Be("inactive");
    }

    [SkippableFact]
    public async Task Read_UnknownStoredValue_ResolvesNullWithWarning()
    {
        // Introduce drift: add a lookup value AND a referencing row AFTER the
        // model (and its enum snapshot) were built, so the FK stays satisfied
        // while 'archived' is absent from the loaded enum set.
        await ExecuteRawAsync("INSERT INTO status (code) VALUES ('archived')");
        await ExecuteRawAsync("INSERT INTO orders (status) VALUES ('archived')");

        // The drift row is the most recently inserted, so it sorts last by id.
        var rows = QueryRows(
            await ExecuteQueryAsync("query { orders(sort: [id_desc], limit: 1) { data { id status } } }"),
            "orders");

        rows.Should().ContainSingle();
        // Drift resolves to null (a warning, not a hard error) — QueryRows
        // already asserts the response carries no errors.
        rows[0]["status"].ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ----- helpers -----------------------------------------------------------

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    private static List<Dictionary<string, JsonElement>> QueryRows(ExecutionResult result, string field)
    {
        result.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        var paged = doc.RootElement.GetProperty("data").GetProperty(field).GetProperty("data");
        return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(paged.GetRawText())!;
    }

    private static int MutationScalar(ExecutionResult result, string field)
    {
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty(field).GetInt32();
    }

    private async Task ExecuteRawAsync(string sql)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadRawStatusAsync(int id)
    {
        await using var cmd = Connection.CreateCommand();
        // id is an int produced by our own insert — no injection surface.
        cmd.CommandText = $"SELECT status FROM orders WHERE id = {id}";
        var value = await cmd.ExecuteScalarAsync();
        return value as string;
    }
}

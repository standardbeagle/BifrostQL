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
    public async Task Update_ByEnumName_PersistsUnderlyingValue()
    {
        // Flip an existing ACTIVE order to INACTIVE through the update path.
        var existing = QueryRows(
            await ExecuteQueryAsync("query { orders(filter: { status: { _eq: ACTIVE } }, limit: 1) { data { id status } } }"),
            "orders");
        existing.Should().NotBeEmpty();
        var id = existing[0]["id"].GetInt32();

        var update = await ExecuteQueryAsync($"mutation {{ orders(update: {{ id: {id}, status: INACTIVE }}) }}");
        update.Errors.Should().BeNullOrEmpty();

        // The write transformer must map the enum name back to the DB value:
        // the raw stored string is 'inactive', not 'INACTIVE'.
        var raw = await ReadRawStatusAsync(id);
        raw.Should().Be("inactive");
    }

    [SkippableFact]
    public async Task Upsert_ByEnumName_PersistsUnderlyingValue()
    {
        // Upsert (keyed by primary key) an existing ACTIVE order to INACTIVE.
        // Exercises the native single-statement upsert path as well as the
        // update/insert fallback (dialect-dependent); both must map the enum.
        var existing = QueryRows(
            await ExecuteQueryAsync("query { orders(filter: { status: { _eq: ACTIVE } }, limit: 1) { data { id status } } }"),
            "orders");
        existing.Should().NotBeEmpty();
        var id = existing[0]["id"].GetInt32();

        var upsert = await ExecuteQueryAsync($"mutation {{ orders(upsert: {{ id: {id}, status: INACTIVE }}) }}");
        upsert.Errors.Should().BeNullOrEmpty();

        // The raw stored string must be the mapped DB value, never the enum name.
        var raw = await ReadRawStatusAsync(id);
        raw.Should().Be("inactive");
    }

    [SkippableFact]
    public async Task Delete_ByEnumName_MatchesUnderlyingValue()
    {
        // The delete predicate carries the primary key plus an enum-column
        // guard. The guard's enum name must be rewritten to the DB value
        // ('inactive') so the row matches — otherwise WHERE ... AND
        // status='INACTIVE' matches nothing and the delete silently reports 0.
        var target = QueryRows(
            await ExecuteQueryAsync("query { orders(filter: { status: { _eq: INACTIVE } }, limit: 1) { data { id status } } }"),
            "orders");
        target.Should().NotBeEmpty();
        var id = target[0]["id"].GetInt32();

        var delete = await ExecuteQueryAsync($"mutation {{ orders(delete: {{ id: {id}, status: INACTIVE }}) }}");
        delete.Errors.Should().BeNullOrEmpty();
        var affected = MutationScalar(delete, "orders");
        affected.Should().Be(1);

        // The targeted row is gone.
        var remaining = QueryRows(
            await ExecuteQueryAsync($"query {{ orders(filter: {{ id: {{ _eq: {id} }} }}) {{ data {{ id }} }} }}"),
            "orders");
        remaining.Should().BeEmpty();
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

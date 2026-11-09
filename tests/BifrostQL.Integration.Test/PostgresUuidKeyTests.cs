using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Operations;
using BifrostQL.Ngsql;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A GraphQL client can only send a <c>uuid</c> as a string, and Npgsql binds a string as
/// <c>text</c>, which PostgreSQL will not compare to a <c>uuid</c>. These run the text form through
/// a real server: every generated mutation over HTTP, and every context call of a server operation.
/// </summary>
public sealed class PostgresUuidKeyTests : IAsyncLifetime
{
    private const string PersonOne = "11111111-1111-1111-1111-111111111111";
    private const string PersonTwo = "22222222-2222-2222-2222-222222222222";
    private const string EntryOne = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Absent = "99999999-9999-9999-9999-999999999999";
    private const string SecondAbsent = "88888888-8888-8888-8888-888888888888";
    private const string Malformed = "not-a-uuid";

    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    private string _dbName = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        if (Master is null) return;
        _dbName = $"bifrost_uuid_key_{Guid.NewGuid():N}";
        await ExecAsync(Master, $"CREATE DATABASE \"{_dbName}\"");
        _connectionString = new NpgsqlConnectionStringBuilder(Master) { Database = _dbName }.ConnectionString;
        await ExecAsync(_connectionString,
            $"""
            CREATE TABLE people (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL);
            CREATE TABLE time_entries (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                person_id UUID NOT NULL REFERENCES people (id),
                note TEXT NOT NULL);
            INSERT INTO people (id, name) VALUES ('{PersonOne}', 'one'), ('{PersonTwo}', 'two');
            INSERT INTO time_entries (id, person_id, note) VALUES ('{EntryOne}', '{PersonOne}', 'first');
            CREATE TABLE write_statements (id SERIAL PRIMARY KEY, verb TEXT NOT NULL, row_count INT NOT NULL);
            CREATE FUNCTION record_write_statement() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                INSERT INTO write_statements (verb, row_count) SELECT TG_ARGV[0], COUNT(*) FROM changed;
                RETURN NULL;
            END $$;
            CREATE TRIGGER record_insert_statement AFTER INSERT ON time_entries
                REFERENCING NEW TABLE AS changed FOR EACH STATEMENT EXECUTE FUNCTION record_write_statement('I');
            CREATE TRIGGER record_update_statement AFTER UPDATE ON time_entries
                REFERENCING NEW TABLE AS changed FOR EACH STATEMENT EXECUTE FUNCTION record_write_statement('U');
            CREATE TRIGGER record_delete_statement AFTER DELETE ON time_entries
                REFERENCING OLD TABLE AS changed FOR EACH STATEMENT EXECUTE FUNCTION record_write_statement('D');
            """);
    }

    public async Task DisposeAsync()
    {
        if (Master is null) return;
        NpgsqlConnection.ClearAllPools();
        await ExecAsync(Master, $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    // ---- generated mutations, over HTTP ----

    [SkippableFact]
    public async Task GeneratedUpdate_FindsTheRowByItsTextKey()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { people(update: { id: "{{PersonOne}}", name: "renamed" }) }""");

        AssertNoErrors(reply);
        (await PeopleAsync()).Should().Equal($"{PersonOne}|renamed", $"{PersonTwo}|two");
    }

    [SkippableFact]
    public async Task GeneratedUpdate_TreatsAnUppercaseTextKeyAsTheSameRow()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { time_entries(update: { id: "{{EntryOne.ToUpperInvariant()}}", note: "edited" }) }""");

        AssertNoErrors(reply);
        (await EntriesAsync()).Should().Equal($"{EntryOne}|{PersonOne}|edited");
    }

    [SkippableFact]
    public async Task GeneratedUpdate_WritesATextForeignKey()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { time_entries(update: { id: "{{EntryOne}}", person_id: "{{PersonTwo}}" }) }""");

        AssertNoErrors(reply);
        (await EntriesAsync()).Should().Equal($"{EntryOne}|{PersonTwo}|first");
    }

    [SkippableFact]
    public async Task GeneratedDelete_FindsTheRowByItsTextKey()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { time_entries(delete: { id: "{{EntryOne}}" }) }""");

        AssertNoErrors(reply);
        (await EntriesAsync()).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task GeneratedUpsert_UpdatesTheRowATextKeyFinds()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { people(upsert: { id: "{{PersonOne}}", name: "upserted" }) }""");

        AssertNoErrors(reply);
        (await PeopleAsync()).Should().Equal($"{PersonOne}|upserted", $"{PersonTwo}|two");
    }

    [SkippableFact]
    public async Task GeneratedUpsert_InsertsWhenATextKeyFindsNothing()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { people(upsert: { id: "{{Absent}}", name: "new" }) }""");

        AssertNoErrors(reply);
        (await PeopleAsync()).Should().Equal($"{PersonOne}|one", $"{PersonTwo}|two", $"{Absent}|new");
    }

    [SkippableFact]
    public async Task GeneratedInsert_TakesTextKeys_AndAnswersWithTheKeyAsText()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync($$"""mutation { time_entries(insert: { id: "{{Absent}}", person_id: "{{PersonTwo}}", note: "typed" }) }""");

        AssertNoErrors(reply);
        reply.RootElement.GetProperty("data").GetProperty("time_entries").GetString().Should().Be(Absent);
        (await EntriesAsync()).Should().Contain($"{Absent}|{PersonTwo}|typed");
    }

    [SkippableTheory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task GeneratedBatch_FindsRowsByTextKeys_OnBothPaths(string bulkThreshold)
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync(
            $$"""
            mutation {
                time_entries_batch(actions: [
                    { insert: { id: "{{Absent}}", person_id: "{{PersonTwo}}", note: "batched" } },
                    { update: { id: "{{EntryOne}}", note: "edited" } }
                ])
            }
            """,
            bulkThreshold);

        AssertNoErrors(reply);
        (await EntriesAsync()).Should().BeEquivalentTo($"{EntryOne}|{PersonOne}|edited", $"{Absent}|{PersonTwo}|batched");

        var deleted = await PostAsync(
            $$"""mutation { time_entries_batch(actions: [{ delete: { id: "{{EntryOne}}" } }, { delete: { id: "{{Absent}}" } }]) }""",
            bulkThreshold);

        AssertNoErrors(deleted);
        (await EntriesAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Text keys must not quietly cost a batch the fast path: each document here is one statement,
    /// which the per-row loop could not be.
    /// </summary>
    [SkippableFact]
    public async Task GeneratedBatch_WithTextKeys_StaysOneStatementPerRun()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        AssertNoErrors(await PostAsync(
            $$"""
            mutation {
                time_entries_batch(actions: [
                    { insert: { id: "{{Absent}}", person_id: "{{PersonOne}}", note: "a" } },
                    { insert: { id: "{{SecondAbsent}}", person_id: "{{PersonTwo}}", note: "b" } }
                ])
            }
            """,
            "1"));
        AssertNoErrors(await PostAsync(
            $$"""
            mutation {
                time_entries_batch(actions: [
                    { update: { id: "{{Absent}}", note: "c" } },
                    { update: { id: "{{SecondAbsent}}", note: "d" } }
                ])
            }
            """,
            "1"));
        AssertNoErrors(await PostAsync(
            $$"""mutation { time_entries_batch(actions: [{ delete: { id: "{{Absent}}" } }, { delete: { id: "{{SecondAbsent}}" } }]) }""",
            "1"));

        (await ReadAsync("SELECT verb, row_count FROM write_statements ORDER BY id", reader => $"{reader.GetString(0)}{reader.GetInt32(1)}"))
            .Should().Equal("I2", "U2", "D2");
    }

    // ---- a malformed value is the caller's mistake, named as one ----

    public static IEnumerable<object[]> MalformedDocuments()
    {
        yield return new object[] { $$"""mutation { people(update: { id: "{{Malformed}}", name: "x" }) }""" };
        yield return new object[] { $$"""mutation { time_entries(update: { id: "{{EntryOne}}", person_id: "{{Malformed}}" }) }""" };
        yield return new object[] { $$"""mutation { people(delete: { id: "{{Malformed}}" }) }""" };
        yield return new object[] { $$"""mutation { people(upsert: { id: "{{Malformed}}", name: "x" }) }""" };
        yield return new object[] { $$"""mutation { people(insert: { id: "{{Malformed}}", name: "x" }) }""" };
        yield return new object[] { $$"""mutation { time_entries(insert: { person_id: "{{Malformed}}", note: "x" }) }""" };
        yield return new object[] { $$"""{ time_entries(filter: { id: { _eq: "{{Malformed}}" } }) { data { id } } }""" };
    }

    [SkippableTheory]
    [MemberData(nameof(MalformedDocuments))]
    public async Task GeneratedDocument_NamesAMalformedUuid_AndWritesNothing(string document)
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync(document);

        AssertInvalidValue(reply);
        await AssertNothingChangedAsync();
    }

    [SkippableTheory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task GeneratedBatch_NamesAMalformedUuid_AndWritesNothing_OnBothPaths(string bulkThreshold)
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        foreach (var action in new[]
                 {
                     $$"""{ update: { id: "{{Malformed}}", note: "x" } }""",
                     $$"""{ delete: { id: "{{Malformed}}" } }""",
                     $$"""{ insert: { person_id: "{{Malformed}}", note: "x" } }""",
                 })
        {
            var reply = await PostAsync(
                $$"""mutation { time_entries_batch(actions: [{ update: { id: "{{EntryOne}}", note: "must roll back" } }, {{action}}]) }""",
                bulkThreshold);

            AssertInvalidValue(reply);
            await AssertNothingChangedAsync();
        }
    }

    [SkippableTheory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("lock")]
    [InlineData("query")]
    [InlineData("queryByForeignKey")]
    [InlineData("insert")]
    public async Task Operation_NamesAMalformedUuid_AndWritesNothing(string call)
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync(OperationCall(call, Malformed));

        AssertInvalidValue(reply);
        await AssertNothingChangedAsync();
    }

    private static void AssertInvalidValue(JsonDocument reply)
    {
        var raw = reply.RootElement.GetRawText();
        ErrorCode(reply).Should().Be("INVALID_VALUE", raw);
        raw.Should().NotContain("invalid input syntax").And.NotContain("22P02");
    }

    private async Task AssertNothingChangedAsync()
    {
        (await PeopleAsync()).Should().Equal($"{PersonOne}|one", $"{PersonTwo}|two");
        (await EntriesAsync()).Should().Equal($"{EntryOne}|{PersonOne}|first");
    }

    // ---- server operations ----

    [SkippableTheory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("lock")]
    [InlineData("query")]
    [InlineData("queryByForeignKey")]
    [InlineData("insert")]
    public async Task Operation_TakesATextKey(string call)
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync(OperationCall(call, call == "queryByForeignKey" ? PersonOne : EntryOne));

        AssertNoErrors(reply);
        reply.RootElement.GetProperty("data").GetProperty("uuidKeyProbe").GetString()
            .Should().Be(UuidKeyProbe.ExpectedAnswer(call));
    }

    [SkippableFact]
    public async Task Operation_LockRow_TakesAGuidKeyToo()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync(OperationCall("lockByGuid", EntryOne));

        AssertNoErrors(reply);
    }

    [SkippableFact]
    public async Task Operation_LockRow_StillNamesAMissingRow()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reply = await PostAsync(OperationCall("lock", Absent));

        ErrorCode(reply).Should().Be(BifrostOperationContext.LockRowNotFoundCode);
    }

    private static string OperationCall(string call, string id)
        => $$"""mutation { uuidKeyProbe(call: "{{call}}", id: "{{id}}") }""";

    /// <summary>Every context call that takes a key, chosen by argument, because an operation is stateless.</summary>
    private sealed class UuidKeyProbe : IBifrostOperation
    {
        public string Name => "uuidKeyProbe";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(call: String!, id: String!): String";

        public static string ExpectedAnswer(string call) => call switch
        {
            "update" or "delete" => "1",
            "insert" => "inserted",
            "lock" => "locked",
            _ => "first",
        };

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            var id = context.GetArgument<string>("id")!;
            switch (context.GetArgument<string>("call"))
            {
                case "update":
                    return (await context.UpdateAsync("time_entries", new Dictionary<string, object?> { ["id"] = id, ["note"] = "edited" })).ToString();
                case "delete":
                    return (await context.DeleteAsync("time_entries", new Dictionary<string, object?> { ["id"] = id })).ToString();
                case "lock":
                    await context.LockRowAsync("time_entries", new Dictionary<string, object?> { ["id"] = id });
                    return "locked";
                case "lockByGuid":
                    await context.LockRowAsync("time_entries", new Dictionary<string, object?> { ["id"] = Guid.Parse(id) });
                    return "locked";
                case "query":
                    return await NoteOfOnlyRowAsync(context, "id", id);
                case "queryByForeignKey":
                    return await NoteOfOnlyRowAsync(context, "person_id", id);
                case "insert":
                    await context.InsertAsync("time_entries", new Dictionary<string, object?>
                    {
                        ["id"] = id == EntryOne ? Absent : id,
                        ["person_id"] = id == EntryOne ? PersonTwo : id,
                        ["note"] = "inserted",
                    });
                    return "inserted";
                default:
                    throw new InvalidOperationException("Unknown probe call.");
            }
        }

        private static async Task<string> NoteOfOnlyRowAsync(BifrostOperationContext context, string column, string value)
        {
            var rows = await context.QueryAsync("time_entries", new Dictionary<string, object?> { [column] = value }, columns: new[] { "note" });
            return (string)rows.Single()["note"]!;
        }
    }

    // ---- harness ----

    private static void AssertNoErrors(JsonDocument reply)
        => reply.RootElement.TryGetProperty("errors", out _).Should().BeFalse(reply.RootElement.GetRawText());

    private static string? ErrorCode(JsonDocument reply)
        => reply.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString();

    private async Task<JsonDocument> PostAsync(string query, string bulkThreshold = "0")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { operations: uuidKeyProbe }",
            ["Bifrost:Metadata:1"] = $"public.time_entries {{ bulk-batch-threshold: {bulkThreshold} }}",
        }).Build();
        DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, connectionString => new PostgresDbConnFactory(connectionString));
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(options => options
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString(_connectionString)
                    .BindProvider("postgres")
                    .AddOperation<UuidKeyProbe>());
            });
            web.Configure(app => app.UseBifrostQL());
        }).StartAsync();
        using var client = host.GetTestClient();

        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/graphql", content);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private Task<List<string>> PeopleAsync()
        => ReadAsync("SELECT id, name FROM people ORDER BY name <> 'one', name <> 'renamed', name <> 'upserted', name <> 'two', name",
            reader => $"{reader.GetGuid(0)}|{reader.GetString(1)}");

    private Task<List<string>> EntriesAsync()
        => ReadAsync("SELECT id, person_id, note FROM time_entries ORDER BY id",
            reader => $"{reader.GetGuid(0)}|{reader.GetGuid(1)}|{reader.GetString(2)}");

    private async Task<List<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> project)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
            rows.Add(project(reader));
        return rows;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

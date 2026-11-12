using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// A write retried with the operation id and the request it was recorded under answers with the
/// recorded result once the caller passes authorization: no second row, no transformer and no
/// hook. A caller the policy now refuses is refused, not replayed.
/// </summary>
public sealed class OperationIdReplayTests : IAsyncLifetime
{
    private const string Writer = "writer";
    private const string Reader = "reader";
    private const string Editor = "editor";
    private const string Clerk = "clerk";
    private const string Admin = "admin";

    private static int _transformerCalls;
    private static int _hookCalls;

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _transformerCalls = 0;
        _hookCalls = 0;
        _dbPath = Path.Combine(Path.GetTempPath(), $"operation-id-replay-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL);" +
                "CREATE TABLE codes (code TEXT PRIMARY KEY, label TEXT NOT NULL);" +
                "CREATE TABLE operation_ids (tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, " +
                "table_name TEXT NOT NULL, action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, " +
                "result TEXT NULL, created_at TEXT NOT NULL, PRIMARY KEY (tenant, caller, operation_id));" +
                "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL);" +
                "CREATE TABLE memos (id INTEGER PRIMARY KEY, body TEXT NULL);" +
                "CREATE TABLE tickets (id INTEGER PRIMARY KEY, status TEXT NOT NULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { operation-id-store: main.operation_ids }",
            ["Bifrost:Metadata:1"] = "main.operation_ids { visibility: hidden }",
            ["Bifrost:Metadata:2"] = $"main.notes {{ operation-id: optional; policy-actions: read, create[{Writer}], update[{Writer}], delete[{Writer}] }}",
            ["Bifrost:Metadata:3"] = "main.codes { operation-id: optional }",
            ["Bifrost:Metadata:4"] = $":root {{ policy-admin-role: {Admin} }}",
            ["Bifrost:Metadata:5"] = $"main.items {{ operation-id: optional; policy-actions: read, create[{Writer}], update[{Editor}] }}",
            ["Bifrost:Metadata:6"] = $"main.memos {{ operation-id: optional; policy-actions: read, create[{Writer}]; policy-write-deny: body; policy-write-deny-roles: {Clerk} }}",
            ["Bifrost:Metadata:7"] = $"main.tickets {{ operation-id: optional; policy-actions: read, create[{Writer}] }}",
            ["Bifrost:Metadata:8"] = "main.tickets.status { writable-values: open }",
        }).Build();

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IInTransactionMutationHook, CountingHook>();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite")
                    .AddMutationTransformer<CountingTransformer>());
            });
            web.Configure(app =>
            {
                app.Use((context, next) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                    {
                        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
                        foreach (var role in context.Request.Headers["X-Test-Role"])
                            claims.Add(new Claim(ClaimTypes.Role, role!));
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                    }
                    return next(context);
                });
                app.UseBifrostQL();
            });
        }).StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Retried_insert_with_same_operation_id_returns_the_original_key_without_a_second_row()
    {
        const string insert = """mutation { notes(insert: { title: "once" }, _operationId: "op-insert") }""";

        using var first = await SendAsync(insert, Writer);
        using var retry = await SendAsync(insert, Writer);

        var key = Data(first, "notes");
        key.ValueKind.Should().Be(JsonValueKind.Number);
        Data(retry, "notes").GetRawText().Should().Be(key.GetRawText(), retry.RootElement.GetRawText());
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
        _transformerCalls.Should().Be(1, "the replay runs no transformer");
        _hookCalls.Should().Be(1, "the replay runs no hook");
    }

    [Fact]
    public async Task Retry_through_the_header_replays_a_write_recorded_through_the_argument()
    {
        using var first = await SendAsync("""mutation { notes(insert: { title: "header" }, _operationId: "op-header") }""", Writer);
        using var retry = await SendWithHeadersAsync("""mutation { notes(insert: { title: "header" }) }""", Writer,
            ("Idempotency-Key", "op-header"));

        Data(retry, "notes").GetRawText().Should().Be(Data(first, "notes").GetRawText(), retry.RootElement.GetRawText());
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        _hookCalls.Should().Be(1);
    }

    [Fact]
    public async Task Retried_delete_answers_the_recorded_result_not_a_second_run()
    {
        await ExecAsync("INSERT INTO notes (id, title) VALUES (5, 'doomed')");
        const string delete = """mutation { notes(delete: { id: 5 }, _operationId: "op-delete") }""";

        using var first = await SendAsync(delete, Writer);
        using var retry = await SendAsync(delete, Writer);

        // A second real delete of the vanished row would answer 0.
        var recorded = Data(first, "notes").GetRawText();
        recorded.Should().NotBe("0", first.RootElement.GetRawText());
        Data(retry, "notes").GetRawText().Should().Be(recorded, retry.RootElement.GetRawText());
        _hookCalls.Should().Be(1);
    }

    [Fact]
    public async Task Retried_insert_on_a_text_key_replays_the_key_as_text()
    {
        const string insert = """mutation { codes(insert: { code: "abc", label: "first" }, _operationId: "op-code") }""";

        using var first = await SendAsync(insert, Writer);
        using var retry = await SendAsync(insert, Writer);

        Data(first, "codes").GetString().Should().Be("abc");
        var replayed = Data(retry, "codes");
        replayed.ValueKind.Should().Be(JsonValueKind.String, retry.RootElement.GetRawText());
        replayed.GetString().Should().Be("abc");
        (await CountAsync("SELECT count(*) FROM codes")).Should().Be(1);
    }

    [Fact]
    public async Task Caller_the_policy_now_refuses_is_refused_not_replayed()
    {
        const string insert = """mutation { notes(insert: { title: "revoked" }, _operationId: "op-revoked") }""";
        using var first = await SendAsync(insert, Writer);
        Data(first, "notes").ValueKind.Should().Be(JsonValueKind.Number);

        using var retry = await SendAsync(insert, Reader);
        // The same caller writing afresh without an id meets the live refusal; the replay
        // refusal answers exactly like it.
        using var live = await SendAsync("""mutation { notes(insert: { title: "live" }) }""", Reader);

        var refused = FirstError(retry);
        var liveError = FirstError(live);
        refused.GetProperty("extensions").GetProperty("code").GetString().Should().Be("ACCESS_DENIED", retry.RootElement.GetRawText());
        refused.GetProperty("message").GetString().Should().Be(liveError.GetProperty("message").GetString());
        liveError.GetProperty("extensions").GetProperty("code").GetString().Should().Be("ACCESS_DENIED");
        retry.RootElement.GetProperty("data").GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
    }

    public const string ReusedMessage = "This operation id was already used for a different write.";

    [Theory]
    [InlineData("table", """mutation { codes(insert: { code: "x", label: "a" }, _operationId: "op-reused") }""")]
    [InlineData("action", """mutation { notes(delete: { id: 1 }, _operationId: "op-reused") }""")]
    [InlineData("target", """mutation { notes(update: { id: 2, title: "a" }, _operationId: "op-reused") }""")]
    [InlineData("input", """mutation { notes(update: { id: 1, title: "b" }, _operationId: "op-reused") }""")]
    public async Task Same_operation_id_on_another_target_is_refused_as_reused(string differs, string mutation)
    {
        await ExecAsync("INSERT INTO notes (id, title) VALUES (1, 'one'), (2, 'two')");
        using var first = await SendAsync("""mutation { notes(update: { id: 1, title: "a" }, _operationId: "op-reused") }""", Writer);
        Data(first, "notes");

        using var other = await SendAsync(mutation, Writer);

        var error = FirstError(other);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("OPERATION_ID_REUSED", differs + ": " + other.RootElement.GetRawText());
        error.GetProperty("message").GetString().Should().Be(ReusedMessage);
        other.RootElement.GetProperty("data").EnumerateObject().Single().Value.ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("SELECT count(*) FROM notes WHERE (id = 1 AND title = 'a') OR (id = 2 AND title = 'two')")).Should().Be(2);
        (await CountAsync("SELECT count(*) FROM codes")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
        _hookCalls.Should().Be(1, "only the first write ran");
    }

    [Fact]
    public async Task Same_id_and_request_from_another_user_writes_its_own_row()
    {
        const string insert = """mutation { notes(insert: { title: "shared" }, _operationId: "op-shared") }""";

        using var first = await SendAsUserAsync(insert, "u-1", Writer);
        using var second = await SendAsUserAsync(insert, "u-2", Writer);

        Data(second, "notes").GetRawText().Should().NotBe(Data(first, "notes").GetRawText(), second.RootElement.GetRawText());
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(2);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(2);
    }

    [Fact]
    public async Task Upsert_retry_by_a_caller_who_lost_the_update_grant_is_refused()
    {
        const string upsert = """mutation { items(upsert: { id: 7, label: "u" }, _operationId: "op-upsert") }""";
        using var first = await SendAsync(upsert, Writer, Editor);
        Data(first, "items");

        // Holds create but not update: the record does not say which branch ran.
        using var retry = await SendAsync(upsert, Writer);

        FirstError(retry).GetProperty("extensions").GetProperty("code").GetString().Should().Be("ACCESS_DENIED", retry.RootElement.GetRawText());
        retry.RootElement.GetProperty("data").GetProperty("items").ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("SELECT count(*) FROM items")).Should().Be(1);
    }

    [Fact]
    public async Task Retry_writing_a_column_the_caller_is_now_denied_is_refused()
    {
        const string insert = """mutation { memos(insert: { body: "b" }, _operationId: "op-memo") }""";
        using var first = await SendAsync(insert, Writer);
        Data(first, "memos").ValueKind.Should().Be(JsonValueKind.Number);

        using var retry = await SendAsync(insert, Writer, Clerk);
        using var live = await SendAsync("""mutation { memos(insert: { body: "c" }) }""", Writer, Clerk);

        var refused = FirstError(retry);
        refused.GetProperty("extensions").GetProperty("code").GetString().Should().Be("ACCESS_DENIED", retry.RootElement.GetRawText());
        refused.GetProperty("message").GetString().Should().Be(FirstError(live).GetProperty("message").GetString());
        (await CountAsync("SELECT count(*) FROM memos")).Should().Be(1);
    }

    [Fact]
    public async Task Retry_of_a_value_only_an_admin_may_write_is_refused_to_a_non_admin()
    {
        const string insert = """mutation { tickets(insert: { status: "closed" }, _operationId: "op-ticket") }""";
        using var first = await SendAsync(insert, Writer, Admin);
        Data(first, "tickets").ValueKind.Should().Be(JsonValueKind.Number);

        using var retry = await SendAsync(insert, Writer);

        FirstError(retry).GetProperty("extensions").GetProperty("code").GetString().Should().Be("ACCESS_DENIED", retry.RootElement.GetRawText());
        retry.RootElement.GetProperty("data").GetProperty("tickets").ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("SELECT count(*) FROM tickets")).Should().Be(1);
    }

    private Task<JsonDocument> SendAsync(string query, params string[] roles) => SendAsUserAsync(query, "u-1", roles);

    private Task<JsonDocument> SendWithHeadersAsync(string query, string role, params (string Name, string Value)[] headers)
        => SendCoreAsync(query, "u-1", new[] { role }, headers);

    private Task<JsonDocument> SendAsUserAsync(string query, string user, params string[] roles)
        => SendCoreAsync(query, user, roles, Array.Empty<(string, string)>());

    private async Task<JsonDocument> SendCoreAsync(string query, string user, string[] roles, (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        request.Headers.Add("X-Test-User", user);
        foreach (var role in roles)
            request.Headers.Add("X-Test-Role", role);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        using var response = await _client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Data(JsonDocument reply, string field)
    {
        if (reply.RootElement.TryGetProperty("errors", out _))
            throw new Xunit.Sdk.XunitException("Expected no errors: " + reply.RootElement.GetRawText());
        return reply.RootElement.GetProperty("data").GetProperty(field);
    }

    private static JsonElement FirstError(JsonDocument reply)
    {
        if (!reply.RootElement.TryGetProperty("errors", out var errors) || errors.GetArrayLength() == 0)
            throw new Xunit.Sdk.XunitException("Expected an error: " + reply.RootElement.GetRawText());
        return errors[0];
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>An application-band transformer that counts every write to <c>notes</c>.</summary>
    private sealed class CountingTransformer : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => table.DbName == "notes";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            Interlocked.Increment(ref _transformerCalls);
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }

    /// <summary>An in-transaction hook (the seam an audit line is written from) that counts writes to <c>notes</c>.</summary>
    private sealed class CountingHook : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName == "notes";

        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            Interlocked.Increment(ref _hookCalls);
            return ValueTask.CompletedTask;
        }
    }
}

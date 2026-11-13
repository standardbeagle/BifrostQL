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
/// A <c>_batch</c> or a <c>save</c> that carries an operation id runs every action or tree node
/// and the id's record in one transaction: the batch or tree commits with its record or not at
/// all, and a retry with the same id and request answers with the recorded result without a
/// second write, transformer or hook.
/// </summary>
public sealed class OperationIdBatchSaveTests : IAsyncLifetime
{
    private const string Writer = "writer";

    private static int _transformerCalls;
    private static int _hookCalls;

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _transformerCalls = 0;
        _hookCalls = 0;
        _dbPath = Path.Combine(Path.GetTempPath(), $"operation-id-batch-save-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL CHECK (title <> 'bad'));" +
            "CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL);" +
            "CREATE TABLE books (id INTEGER PRIMARY KEY, author_id INTEGER NOT NULL REFERENCES authors(id), " +
            "title TEXT NOT NULL CHECK (title <> 'bad'));" +
            "CREATE TABLE drafts (id INTEGER PRIMARY KEY, author_id INTEGER NOT NULL REFERENCES authors(id), body TEXT NOT NULL);" +
            "CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, \"table\" TEXT NOT NULL, op TEXT NOT NULL, " +
            "intended_payload TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, requester_context TEXT NULL, " +
            "\"state\" TEXT NOT NULL, approver TEXT NULL, decided_at TEXT NULL, reason TEXT NULL);" +
            "CREATE TABLE ledger (id INTEGER PRIMARY KEY, title TEXT NOT NULL);" +
            "CREATE TABLE operation_ids (tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, " +
            "table_name TEXT NOT NULL, action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, " +
            "result TEXT NULL, created_at TEXT NOT NULL, PRIMARY KEY (tenant, caller, operation_id));");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { operation-id-store: main.operation_ids }",
            ["Bifrost:Metadata:1"] = "main.operation_ids { visibility: hidden }",
            ["Bifrost:Metadata:2"] = $"main.notes {{ operation-id: optional; policy-actions: read, create[{Writer}], update[{Writer}], delete[{Writer}] }}",
            ["Bifrost:Metadata:3"] = "main.authors { operation-id: optional }",
            ["Bifrost:Metadata:4"] = "main.drafts { approval: enabled; approver-role: manager }",
            ["Bifrost:Metadata:5"] = "main.ledger { operation-id: required }",
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
    public async Task Retried_batch_with_same_operation_id_replays_without_a_second_write()
    {
        const string batch =
            """mutation { notes_batch(actions: [{ insert: { title: "a" } }, { insert: { title: "b" } }], _operationId: "op-batch") }""";

        using var first = await SendAsync(batch, Writer);
        using var retry = await SendAsync(batch, Writer);

        Data(first, "notes_batch").GetRawText().Should().Be("2");
        Data(retry, "notes_batch").GetRawText().Should().Be("2", retry.RootElement.GetRawText());
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(2);
        (await CountAsync("SELECT count(*) FROM operation_ids WHERE operation_id = 'op-batch' AND result = '2'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
        _transformerCalls.Should().Be(2, "each action of the first batch runs the chain once and the replay runs none");
        _hookCalls.Should().Be(2, "each action of the first batch runs the hook once and the replay runs none");
    }

    [Fact]
    public async Task Retried_save_with_same_operation_id_replays_without_a_second_write()
    {
        const string save =
            """mutation { authors(save: { name: "ann", books: [{ title: "one" }, { title: "two" }] }, _operationId: "op-save") }""";

        using var first = await SendAsync(save, Writer);
        using var retry = await SendAsync(save, Writer);

        var key = Data(first, "authors");
        key.ValueKind.Should().Be(JsonValueKind.Number);
        Data(retry, "authors").GetRawText().Should().Be(key.GetRawText(), retry.RootElement.GetRawText());
        (await CountAsync("SELECT count(*) FROM authors")).Should().Be(1);
        (await CountAsync($"SELECT count(*) FROM books WHERE author_id = {key.GetRawText()}")).Should().Be(2);
        (await CountAsync("SELECT count(*) FROM books")).Should().Be(2);
        (await CountAsync($"SELECT count(*) FROM operation_ids WHERE operation_id = 'op-save' AND result = '{key.GetRawText()}'")).Should().Be(1);
        _transformerCalls.Should().Be(2, "each book of the first save runs the chain once and the replay runs none");
        _hookCalls.Should().Be(2, "each book of the first save runs the hook once and the replay runs none");
    }

    [Fact]
    public async Task Batch_that_fails_records_nothing_so_the_id_stays_free()
    {
        using var failed = await SendAsync(
            """mutation { notes_batch(actions: [{ insert: { title: "good" } }, { insert: { title: "bad" } }], _operationId: "op-fail") }""",
            Writer);

        FirstError(failed);
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);

        using var retry = await SendAsync(
            """mutation { notes_batch(actions: [{ insert: { title: "good" } }], _operationId: "op-fail") }""", Writer);

        Data(retry, "notes_batch").GetRawText().Should().Be("1");
        (await CountAsync("SELECT count(*) FROM notes WHERE title = 'good'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
    }

    [Fact]
    public async Task Save_that_fails_records_nothing_so_the_id_stays_free()
    {
        using var failed = await SendAsync(
            """mutation { authors(save: { name: "ann", books: [{ title: "bad" }] }, _operationId: "op-fail") }""", Writer);

        FirstError(failed);
        (await CountAsync("SELECT count(*) FROM authors")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);

        using var retry = await SendAsync(
            """mutation { authors(save: { name: "ann", books: [{ title: "good" }] }, _operationId: "op-fail") }""", Writer);

        Data(retry, "authors").ValueKind.Should().Be(JsonValueKind.Number);
        (await CountAsync("SELECT count(*) FROM books WHERE title = 'good'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
    }

    [Theory]
    [InlineData("actions", """mutation { notes_batch(actions: [{ insert: { title: "other" } }], _operationId: "op-batch") }""")]
    [InlineData("verb", """mutation { notes(insert: { title: "a" }, _operationId: "op-batch") }""")]
    public async Task Same_operation_id_on_a_different_write_than_the_recorded_batch_is_refused_as_reused(string differs, string mutation)
    {
        using var first = await SendAsync(
            """mutation { notes_batch(actions: [{ insert: { title: "a" } }], _operationId: "op-batch") }""", Writer);
        Data(first, "notes_batch").GetRawText().Should().Be("1");

        using var reused = await SendAsync(mutation, Writer);

        var error = FirstError(reused);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("OPERATION_ID_REUSED", differs);
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
    }

    [Fact]
    public async Task Batch_retry_by_a_caller_who_lost_the_grant_is_refused()
    {
        const string batch = """mutation { notes_batch(actions: [{ insert: { title: "a" } }], _operationId: "op-grant") }""";
        using var first = await SendAsync(batch, Writer);
        Data(first, "notes_batch").GetRawText().Should().Be("1");

        using var retry = await SendAsync(batch);

        FirstError(retry).GetProperty("extensions").GetProperty("code").GetString().Should().Be("ACCESS_DENIED", retry.RootElement.GetRawText());
        retry.RootElement.GetProperty("data").GetProperty("notes_batch").ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
    }

    [Fact]
    public async Task Save_whose_tree_reaches_an_approval_table_is_refused_without_writing()
    {
        using var reply = await SendAsync(
            """mutation { authors(save: { name: "ann", drafts: [{ body: "gated" }] }, _operationId: "op-gated") }""", Writer);

        var error = FirstError(reply);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("OPERATION_ID_UNSUPPORTED", reply.RootElement.GetRawText());
        (await CountAsync("SELECT count(*) FROM authors")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM drafts")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM pending_changes")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [Fact]
    public async Task Save_whose_tree_reaches_an_approval_table_without_an_id_is_still_enqueued()
    {
        // The control for the refusal above: without an id the same tree reaches the gate.
        using var reply = await SendAsync(
            """mutation { authors(save: { name: "ann", drafts: [{ body: "gated" }] }) }""", Writer);

        FirstError(reply);
        (await CountAsync("SELECT count(*) FROM pending_changes")).Should().Be(1);
    }

    [Theory]
    [InlineData("""mutation { ledger_batch(actions: [{ insert: { title: "batched" } }], _operationId: "op-required") }""", "ledger_batch")]
    [InlineData("""mutation { ledger(save: { title: "saved" }, _operationId: "op-required") }""", "ledger")]
    public async Task Batch_and_save_with_operation_id_on_a_required_table_are_recorded(string mutation, string field)
    {
        using var reply = await SendAsync(mutation, Writer);

        Data(reply, field).ValueKind.Should().Be(JsonValueKind.Number);
        (await CountAsync("SELECT count(*) FROM ledger")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids WHERE operation_id = 'op-required'")).Should().Be(1);
    }

    private async Task<JsonDocument> SendAsync(string query, params string[] roles)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        request.Headers.Add("X-Test-User", "u-1");
        foreach (var role in roles)
            request.Headers.Add("X-Test-Role", role);
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

    /// <summary>An application-band transformer that counts every write to <c>notes</c> and <c>books</c>.</summary>
    private sealed class CountingTransformer : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => table.DbName is "notes" or "books";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            Interlocked.Increment(ref _transformerCalls);
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }

    /// <summary>An in-transaction hook that counts writes to <c>notes</c> and <c>books</c>.</summary>
    private sealed class CountingHook : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName is "notes" or "books";

        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            Interlocked.Increment(ref _hookCalls);
            return ValueTask.CompletedTask;
        }
    }
}

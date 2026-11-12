using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
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
/// A table declaring <c>operation-id: required</c> refuses every write that carries no operation
/// id with <c>OPERATION_ID_REQUIRED</c> and writes nothing; a write that carries one is recorded.
/// An <c>optional</c> table still writes without an id.
/// </summary>
public sealed class OperationIdRequiredTests : IAsyncLifetime
{
    private const string RequiredCode = "OPERATION_ID_REQUIRED";
    private const string RequiredMessage = "This write requires an operation id.";

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"operation-id-required-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            "CREATE TABLE ledger (id INTEGER PRIMARY KEY, title TEXT NOT NULL);" +
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL);" +
            "CREATE TABLE operation_ids (tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, " +
            "table_name TEXT NOT NULL, action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, " +
            "result TEXT NULL, created_at TEXT NOT NULL, PRIMARY KEY (tenant, caller, operation_id));" +
            "INSERT INTO ledger (id, title) VALUES (1, 'seed');");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { operation-id-store: main.operation_ids }",
            ["Bifrost:Metadata:1"] = "main.operation_ids { visibility: hidden }",
            ["Bifrost:Metadata:2"] = "main.ledger { operation-id: required }",
            ["Bifrost:Metadata:3"] = "main.notes { operation-id: optional }",
        }).Build();

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite"));
            });
            web.Configure(app => app.UseBifrostQL());
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

    [Theory]
    [InlineData("""mutation { ledger(insert: { title: "new" }) }""")]
    [InlineData("""mutation { ledger(update: { id: 1, title: "changed" }) }""")]
    [InlineData("""mutation { ledger(upsert: { id: 1, title: "changed" }) }""")]
    [InlineData("""mutation { ledger(delete: { id: 1 }) }""")]
    [InlineData("""mutation { ledger_batch(actions: [{ insert: { title: "batched" } }]) }""")]
    public async Task Write_without_operation_id_on_a_required_table_is_refused_with_a_named_code(string mutation)
    {
        using var reply = await SendAsync(mutation);

        var error = FirstError(reply);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(RequiredCode, reply.RootElement.GetRawText());
        error.GetProperty("message").GetString().Should().Be(RequiredMessage);
        reply.RootElement.GetRawText().Should().NotContain("ledger\"").And.NotContain("main.");
        (await CountAsync("SELECT count(*) FROM ledger")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM ledger WHERE id = 1 AND title = 'seed'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [Fact]
    public async Task Adapter_write_through_the_intent_executor_is_refused_on_a_required_table()
    {
        var writes = _host.Services.GetRequiredService<IMutationIntentExecutor>();

        var refused = async () => await writes.ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["title"] = "adapter" },
        });

        var error = (await refused.Should().ThrowAsync<BifrostExecutionError>()).Which;
        error.ErrorCode.Should().Be(RequiredCode);
        error.Message.Should().Be(RequiredMessage);
        (await CountAsync("SELECT count(*) FROM ledger")).Should().Be(1);
    }

    [Fact]
    public async Task Write_with_operation_id_on_a_required_table_is_recorded()
    {
        using var reply = await SendAsync("""mutation { ledger(insert: { title: "kept" }, _operationId: "op-required") }""");

        Data(reply, "ledger").ValueKind.Should().Be(JsonValueKind.Number);
        (await CountAsync("SELECT count(*) FROM ledger WHERE title = 'kept'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids WHERE operation_id = 'op-required'")).Should().Be(1);
    }

    [Fact]
    public async Task Idempotency_key_header_satisfies_a_required_table()
    {
        using var reply = await SendAsync("""mutation { ledger(update: { id: 1, title: "by-header" }) }""",
            ("Idempotency-Key", "op-header"));

        Data(reply, "ledger");
        (await CountAsync("SELECT count(*) FROM ledger WHERE id = 1 AND title = 'by-header'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids WHERE operation_id = 'op-header'")).Should().Be(1);
    }

    [Fact]
    public async Task Optional_table_still_writes_without_an_operation_id()
    {
        using var reply = await SendAsync("""mutation { notes(insert: { title: "free" }) }""");

        Data(reply, "notes");
        (await CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    private async Task<JsonDocument> SendAsync(string query, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
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
}

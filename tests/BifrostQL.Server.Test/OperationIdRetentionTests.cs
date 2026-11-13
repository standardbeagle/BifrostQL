using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Retention;
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
/// How long the operation-id store keeps a record. The store is an ordinary hidden table, so a
/// <c>ttl: &lt;window&gt; after created_at</c> on it expires records through the retention purge
/// engine; a retry of a purged id runs as a fresh write. A store without a <c>ttl</c> keeps every
/// record. A write that changed no row is recorded like any other, so its retry answers the
/// recorded no-op even after the target row appears.
/// </summary>
public sealed class OperationIdRetentionTests : IAsyncLifetime
{
    private const string Expired = "2000-01-01 00:00:00";

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"operation-id-retention-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL);" +
            "CREATE TABLE operation_ids (tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, " +
            "table_name TEXT NOT NULL, action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, " +
            "result TEXT NULL, created_at DATETIME NOT NULL, PRIMARY KEY (tenant, caller, operation_id));");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { operation-id-store: main.operation_ids }",
            ["Bifrost:Metadata:1"] = "main.operation_ids { visibility: hidden; ttl: 1d after created_at }",
            ["Bifrost:Metadata:2"] = "main.notes { operation-id: optional }",
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
            web.Configure(app =>
            {
                app.Use((context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "u-1") }, "test"));
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
    public async Task Record_older_than_retention_is_purged_and_retry_executes_fresh()
    {
        const string aged = """mutation { notes(insert: { title: "aged" }, _operationId: "op-aged") }""";
        const string recent = """mutation { notes(insert: { title: "recent" }, _operationId: "op-recent") }""";
        using var agedFirst = await SendAsync(aged);
        using var recentFirst = await SendAsync(recent);
        var agedKey = Data(agedFirst, "notes").GetInt64();
        var recentKey = Data(recentFirst, "notes").GetInt64();
        await ExecAsync($"UPDATE operation_ids SET created_at = '{Expired}' WHERE operation_id = 'op-aged'");

        await _host.Services.GetRequiredService<RetentionPurgeEngine>().PurgeEndpointsOnceAsync(CancellationToken.None);

        (await CountAsync("SELECT count(*) FROM operation_ids WHERE operation_id = 'op-aged'")).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM operation_ids WHERE operation_id = 'op-recent'")).Should().Be(1);

        // The purged id runs as a fresh write: a second row and a new record. The recent id still
        // replays its recorded key without writing.
        using var agedRetry = await SendAsync(aged);
        using var recentRetry = await SendAsync(recent);
        Data(agedRetry, "notes").GetInt64().Should().NotBe(agedKey);
        Data(recentRetry, "notes").GetInt64().Should().Be(recentKey);
        (await CountAsync("SELECT count(*) FROM notes WHERE title = 'aged'")).Should().Be(2);
        (await CountAsync("SELECT count(*) FROM notes WHERE title = 'recent'")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(2);
    }

    private async Task<JsonDocument> SendAsync(string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Data(JsonDocument reply, string field)
    {
        if (reply.RootElement.TryGetProperty("errors", out _))
            throw new Xunit.Sdk.XunitException("Expected no errors: " + reply.RootElement.GetRawText());
        return reply.RootElement.GetProperty("data").GetProperty(field);
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

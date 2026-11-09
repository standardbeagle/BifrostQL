using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Integration.Test;

/// <summary>
/// SQLite keeps a column declared <c>UUID</c> as text, so two spellings of one uuid are two rows
/// there. A batch must address both, because merging them loses a write silently. SQLite has no
/// bulk executor, so the per-row path is the only one a batch takes here.
/// </summary>
public sealed class SqliteDeclaredUuidColumnTests : IAsyncLifetime
{
    private const string Lower = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private static readonly string Upper = Lower.ToUpperInvariant();

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bifrost-sqlite-uuid-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_path};Pooling=False";

    public async Task InitializeAsync() => await ExecAsync($"""
        CREATE TABLE items (id UUID PRIMARY KEY, note TEXT);
        INSERT INTO items (id, note) VALUES ('{Upper}', 'orig'), ('{Lower}', 'orig');
        """);

    public Task DisposeAsync()
    {
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GeneratedBatch_UpdatesBothRows_WhenTheirIdsDifferOnlyInCase()
    {
        var reply = await PostAsync(
            $$"""
            mutation {
                items_batch(actions: [
                    { update: { id: "{{Upper}}", note: "upper" } },
                    { update: { id: "{{Lower}}", note: "lower" } }
                ])
            }
            """);

        reply.RootElement.GetProperty("data").GetProperty("items_batch").GetInt32().Should().Be(2, reply.RootElement.GetRawText());
        (await RowsAsync()).Should().Equal($"{Upper}=upper", $"{Lower}=lower");
    }

    [Fact]
    public async Task GeneratedBatch_ADeleteLeavesTheOtherSpellingsUpdate()
    {
        var reply = await PostAsync(
            $$"""
            mutation {
                items_batch(actions: [
                    { update: { id: "{{Upper}}", note: "kept" } },
                    { delete: { id: "{{Lower}}" } }
                ])
            }
            """);

        reply.RootElement.TryGetProperty("errors", out _).Should().BeFalse(reply.RootElement.GetRawText());
        (await RowsAsync()).Should().Equal($"{Upper}=kept");
    }

    [Fact]
    public async Task GeneratedBatch_RejectPolicy_RefusesNothing_WhenTheIdsAreTwoRows()
    {
        var reply = await PostAsync(
            $$"""
            mutation {
                items_batch(actions: [
                    { update: { id: "{{Upper}}", note: "upper" } },
                    { update: { id: "{{Lower}}", note: "lower" } }
                ])
            }
            """,
            duplicatePolicy: "reject");

        reply.RootElement.TryGetProperty("errors", out _).Should().BeFalse(reply.RootElement.GetRawText());
        (await RowsAsync()).Should().Equal($"{Upper}=upper", $"{Lower}=lower");
    }

    private async Task<JsonDocument> PostAsync(string query, string duplicatePolicy = "last-wins")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = $"main.items {{ batch-duplicate-policy: {duplicatePolicy} }}",
        }).Build();
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(o => o.BindConfiguration(config.GetSection("Bifrost")).BindConnectionString(ConnectionString).BindProvider("sqlite"));
            });
            web.Configure(app => app.UseBifrostQL());
        }).StartAsync();
        using var client = host.GetTestClient();
        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/graphql", content);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<List<string>> RowsAsync()
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id || '=' || note FROM items ORDER BY id";
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
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
/// Runs the exact HTTP bodies <c>useBifrostBatch</c> sends against a real server. The React
/// package's own tests mock the network, and a mock-only suite is how an earlier release
/// shipped queries every real server rejected, so the bodies live in one file both stacks
/// read: the vitest suite pins the hook to it, this suite pins the server to it.
/// </summary>
public sealed class ReactBatchWireContractTests : IAsyncLifetime
{
    private static readonly string ContractPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "batch-wire-contract.json");

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"react-batch-contract-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            """
            CREATE TABLE items (id INTEGER PRIMARY KEY, sku TEXT NOT NULL UNIQUE, name TEXT NOT NULL);
            INSERT INTO items (id, sku, name) VALUES (1, 'sku-1', 'one'), (2, 'sku-2', 'two'), (3, 'sku-3', 'three');
            """);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
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

    public Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MixedDocument_AppliesEveryVerb_AndRepliesWithTheAffectedRowCount()
    {
        // Arrange
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath));
        var mixed = contract.RootElement.GetProperty("mixed");

        // Act
        using var reply = await PostAsync(mixed.GetProperty("request"));

        // Assert: the reply is the one the hook's tests are fed.
        reply.RootElement.TryGetProperty("errors", out _).Should().BeFalse(reply.RootElement.GetRawText());
        reply.RootElement.GetProperty("data").GetProperty("items_batch").GetInt32()
            .Should().Be(mixed.GetProperty("reply").GetProperty("data").GetProperty("items_batch").GetInt32());
        (await RowsAsync()).Should().Equal(
            "1|sku-1|upserted",
            "2|sku-2|renamed",
            "4|sku-new|new");
    }

    [Fact]
    public async Task FailingDocument_WritesNothing_AndRepliesWithTheShapeTheHookReads()
    {
        // Arrange: the insert ahead of the duplicate has already run when the duplicate fails.
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath));
        var failing = contract.RootElement.GetProperty("failing");
        var before = await RowsAsync();

        // Act
        using var reply = await PostAsync(failing.GetProperty("request"));

        // Assert
        reply.RootElement.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        reply.RootElement.GetProperty("errors")[0].GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        reply.RootElement.GetProperty("data").GetProperty("items_batch").ValueKind.Should().Be(JsonValueKind.Null);
        (await RowsAsync()).Should().Equal(before, "one transaction: the rows ahead of the failure roll back");
    }

    private async Task<JsonDocument> PostAsync(JsonElement requestBody)
    {
        using var content = new StringContent(requestBody.GetRawText(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _client.PostAsync("/graphql", content);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> RowsAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, sku, name FROM items ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetInt64(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        return rows;
    }
}

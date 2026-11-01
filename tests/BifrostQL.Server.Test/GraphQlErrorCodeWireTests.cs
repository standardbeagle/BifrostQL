using System.Net.Http.Json;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The documented error codes reach a GraphQL client as <c>errors[].extensions.code</c>. Before
/// the fix every Bifrost error left the HTTP endpoint as <c>BIFROST_EXECUTION_ERROR</c>, so a
/// policy denial, a pending approval and a fault were told apart only by message text.
/// </summary>
public sealed class GraphQlErrorCodeWireTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"error-code-wire-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL);" +
                "CREATE TABLE drafts (id INTEGER PRIMARY KEY, body TEXT NOT NULL);" +
                "CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, \"table\" TEXT NOT NULL, op TEXT NOT NULL, " +
                "intended_payload TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, requester_context TEXT NULL, " +
                "\"state\" TEXT NOT NULL, approver TEXT NULL, decided_at TEXT NULL, reason TEXT NULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = "main.notes { policy-actions: read }",
            ["Bifrost:Metadata:1"] = "main.drafts { approval: enabled; approver-role: manager }",
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
    [InlineData("mutation { notes(insert: { body: \"x\" }) }", "ACCESS_DENIED")]
    [InlineData("mutation { drafts(insert: { body: \"x\" }) }", "PENDING_APPROVAL")]
    public async Task ErrorCode_IsTheWireExtensionsCode(string mutation, string expectedCode)
    {
        var response = await _client.PostAsync("/graphql", JsonContent.Create(new { query = mutation }));
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("errors").EnumerateArray().Single();
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(expectedCode, body);
    }
}

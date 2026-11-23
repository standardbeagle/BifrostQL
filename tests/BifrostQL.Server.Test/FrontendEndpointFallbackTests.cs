using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// <c>BifrostEngine</c> refuses a request whose mount path is not a registered GraphQL endpoint
/// when several are registered. Since <c>UseBifrostBinary</c> refuses an unset or unknown
/// <c>graphqlPath</c> at mount time (MountEndpointSelectionTests), only a protocol frontend
/// mounted at a non-endpoint path reaches that refusal, so its text must name that mount and not
/// tell the operator to configure the binary transport.
/// </summary>
public sealed class FrontendEndpointFallbackTests : IAsyncLifetime
{
    private const string FrontendPath = "/front";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"frontend-fallback-{Guid.NewGuid():N}.db");
    private IHost? _host;

    public Task InitializeAsync()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL);";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<JsonElement> PostWidgetsAsync(string mountPath, params string[] endpointPaths)
    {
        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddBifrostEndpoints(o =>
                {
                    foreach (var path in endpointPaths)
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = $"Data Source={_dbPath}";
                            e.Provider = "sqlite";
                            e.Path = path;
                            e.DisableAuth = true;
                        });
                });
                services.AddBifrostEngine();
            });
            web.Configure(app => app.UseBifrostGraphQL(mountPath, requireAuthentication: false));
        }).StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, mountPath)
        {
            Content = new StringContent("{\"query\":\"{ widgets { data { id } } }\"}", Encoding.UTF8, "application/json"),
        };
        using var response = await _host.GetTestClient().SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task FrontendAtANonEndpointPath_OnAMultiEndpointHost_NamesTheFrontendMount()
    {
        var body = await PostWidgetsAsync(FrontendPath, "/graphql", "/other");

        body.GetProperty("errors")[0].GetProperty("message").GetString().Should().Be(
            "This mount's path is not a registered GraphQL endpoint and more than one endpoint is " +
            "registered, so it has no target database. Mount UseBifrostFrontend / UseBifrostGraphQL " +
            "at the path of the endpoint it serves.");
    }

    [Fact]
    public async Task FrontendAtANonEndpointPath_OnASingleEndpointHost_ServesTheOnlyEndpoint()
    {
        // Control: the fallback the refusal guards is still taken with one endpoint.
        var body = await PostWidgetsAsync(FrontendPath, "/graphql");

        body.TryGetProperty("errors", out _).Should().BeFalse(body.ToString());
        body.GetProperty("data").GetProperty("widgets").GetProperty("data").GetArrayLength().Should().Be(0);
    }
}

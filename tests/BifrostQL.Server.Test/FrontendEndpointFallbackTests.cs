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
/// <c>UseBifrostFrontend</c> / <c>UseBifrostGraphQL</c> key their endpoint by the mount path. A
/// mount at a path that is not a registered endpoint serves the single endpoint when exactly one
/// is registered; with several, it has no target and used to start and fail every request with
/// <c>BifrostEngine</c>'s ambiguity error. It now refuses at mount time, like every front door
/// that names its endpoint (MountEndpointSelectionTests).
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
    public async Task FrontendAtANonEndpointPath_OnAMultiEndpointHost_RefusesToStart()
    {
        var start = () => PostWidgetsAsync(FrontendPath, "/graphql", "/other");

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("UseBifrostFrontend: path '/front' is not a registered GraphQL endpoint*2*");
    }

    [Fact]
    public async Task FrontendAtARegisteredEndpointPath_OnAMultiEndpointHost_ServesThatEndpoint()
    {
        // Control: naming a registered endpoint (any case, as app.Map matches) still starts.
        var body = await PostWidgetsAsync("/Other", "/graphql", "/other");

        body.TryGetProperty("errors", out _).Should().BeFalse(body.ToString());
        body.GetProperty("data").GetProperty("widgets").GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Engine_CalledDirectlyWithANonEndpointPath_OnAMultiEndpointHost_RefusesTheRequest()
    {
        // The engine keeps its own refusal for hosts that call IBifrostEngine directly.
        await PostWidgetsAsync("/graphql", "/graphql", "/other");
        var engine = _host!.Services.GetRequiredService<BifrostQL.Core.Resolvers.IBifrostEngine>();

        var result = await engine.ExecuteAsync(
            new BifrostQL.Core.Resolvers.BifrostRequest { Query = "{ widgets { data { id } } }" }, FrontendPath);

        result.Errors.Should().ContainSingle().Which.Message.Should().Be(
            "The endpoint path is not a registered GraphQL endpoint and more than one endpoint is " +
            "registered, so it has no target database. Pass the path of the endpoint to serve.");
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

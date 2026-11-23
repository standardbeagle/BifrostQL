using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Mcp.Test;

/// <summary>
/// <c>AddBifrostMcpHttp(endpoint:)</c> chooses its GraphQL endpoint at host start, as every other
/// front door does (MountEndpointSelectionTests in BifrostQL.Server.Test). A misspelled name, or
/// none named on a multi-endpoint host, used to start the host and fail every tool call.
/// </summary>
public sealed class McpHttpEndpointSelectionTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mcp-endpoint-{Guid.NewGuid():N}.db");
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

    private Task<IHost> StartAsync(string? endpoint, params string[] endpointPaths)
        => new HostBuilder().ConfigureWebHost(web =>
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
                services.AddRouting();
                services.AddBifrostMcpHttp(new McpAuthOptions { Mode = McpAuthMode.FailClosed }, endpoint);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapBifrostMcp("/mcp"));
            });
        }).StartAsync();

    [Fact]
    public async Task McpHttp_RefusesToStart_WhenSeveralEndpointsAreRegisteredAndNoneIsNamed()
    {
        var start = () => StartAsync(null, "/graphql", "/other");

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("AddBifrostMcpHttp:*endpoint*");
    }

    [Fact]
    public async Task McpHttp_RefusesToStart_WhenTheNamedEndpointIsNotRegistered()
    {
        var start = () => StartAsync("/grapql", "/graphql");

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("AddBifrostMcpHttp:*'/grapql'*");
    }

    [Fact]
    public async Task McpHttp_Starts_WhenItNamesARegisteredEndpoint_OnAMultiEndpointHost()
    {
        _host = await StartAsync("/GraphQL", "/graphql", "/other");

        _host.Should().NotBeNull();
    }

    [Fact]
    public async Task McpHttp_Starts_WhenNoneIsNamed_OnASingleEndpointHost()
    {
        _host = await StartAsync(null, "/graphql");

        _host.Should().NotBeNull();
    }
}

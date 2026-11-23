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
/// The HTTP mounts that serve a host-configured GraphQL endpoint (<c>/_blob</c>,
/// <c>/_app-metadata</c>, the binary WebSocket) choose it at mount time, as
/// <c>/_saved-objects</c> does (<see cref="SavedObjectsEndpointSelectionTests"/>). A misspelled
/// name, or none named on a multi-endpoint host, used to pass the mount because
/// MountAuthRequirement's single-endpoint fallback resolved an auth requirement anyway; every
/// request then failed (blob 503, app-metadata unhandled 500, binary a result error).
/// </summary>
public sealed class MountEndpointSelectionTests : IAsyncLifetime
{
    public enum Mount { Blob, AppMetadata, Binary }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mount-endpoint-{Guid.NewGuid():N}.db");
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

    private Task<IHost> StartAsync(Mount mount, string? endpoint, params string[] endpointPaths)
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
                services.AddBifrostEngine();
            });
            web.Configure(app =>
            {
                app.UseBifrostEndpoints();
                switch (mount)
                {
                    case Mount.Blob:
                        app.UseBifrostBlobs(o => o.Endpoint = endpoint);
                        break;
                    case Mount.AppMetadata:
                        app.UseBifrostAppMetadata(o => o.GraphQlEndpoint = endpoint);
                        break;
                    case Mount.Binary:
                        app.UseWebSockets();
                        app.UseBifrostBinary(graphqlPath: endpoint);
                        break;
                }
            });
        }).StartAsync();

    [Theory]
    [InlineData(Mount.Blob, "*Endpoint*")]
    [InlineData(Mount.AppMetadata, "*GraphQlEndpoint*")]
    [InlineData(Mount.Binary, "*graphqlPath*")]
    public async Task Mount_RefusesToStart_WhenSeveralEndpointsAreRegisteredAndNoneIsNamed(Mount mount, string message)
    {
        var start = () => StartAsync(mount, null, "/graphql", "/other");

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage(message);
    }

    [Theory]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Binary)]
    public async Task Mount_RefusesToStart_WhenTheNamedEndpointIsNotRegistered(Mount mount)
    {
        // A single registered endpoint is exactly where the auth fallback masked the typo.
        var start = () => StartAsync(mount, "/grapql", "/graphql");

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*'/grapql'*");
    }

    [Theory]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Binary)]
    public async Task Mount_Starts_WhenItNamesARegisteredEndpoint_OnAMultiEndpointHost(Mount mount)
    {
        // Control for both refusals: a registered name (any case) mounts.
        _host = await StartAsync(mount, "/GraphQL", "/graphql", "/other");

        _host.Should().NotBeNull();
    }

    [Theory]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Binary)]
    public async Task Mount_Starts_WhenNoneIsNamed_OnASingleEndpointHost(Mount mount)
    {
        // Null still selects the single registered endpoint (the binary mount's default).
        _host = await StartAsync(mount, null, "/graphql");

        _host.Should().NotBeNull();
    }
}

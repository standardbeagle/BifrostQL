using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Server.Feeds;
using BifrostQL.Server.Ldap;
using BifrostQL.Server.OData;
using BifrostQL.Server.Pgwire;
using BifrostQL.Server.Prometheus;
using BifrostQL.Server.Resp;
using BifrostQL.Server.S3;
using BifrostQL.Server.Test.Ldap;
using BifrostQL.Server.Test.Pgwire;
using BifrostQL.Server.Test.Resp;
using BifrostQL.Server.Test.S3;
using BifrostQL.Sqlite;
using NSubstitute;
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
/// Every front door that serves a host-configured GraphQL endpoint chooses it at startup, as
/// <c>/_saved-objects</c> does (<see cref="SavedObjectsEndpointSelectionTests"/>). A misspelled
/// name, or none named on a multi-endpoint host, used to start the front door and fail every
/// request: the HTTP mounts (blob 503, app-metadata unhandled 500, binary a result error) because
/// MountAuthRequirement's single-endpoint fallback resolved an auth requirement anyway, the rest
/// (chat, S3, OData, feeds, Prometheus, pgwire, RESP, LDAP) because nothing checked the name
/// until a request resolved the model. The raw-wire listeners refuse when the host starts its
/// hosted services, before any listener binds. gRPC already refused at
/// <c>MapBifrostGrpcAsync</c>, which loads the model; MCP over HTTP is pinned in BifrostQL.Mcp.Test.
/// </summary>
public sealed class MountEndpointSelectionTests : IAsyncLifetime
{
    public enum Mount { Blob, AppMetadata, Binary, Chat, S3, OData, Feeds, Prometheus, Pgwire, Resp, Ldap }

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
                    if (mount == Mount.S3)
                        o.AddS3Endpoint(s3 => s3.Endpoint = endpoint);
                    if (mount == Mount.OData)
                        o.AddODataEndpoint(odata => odata.Endpoint = endpoint);
                });
                services.AddBifrostEngine();
                switch (mount)
                {
                    case Mount.Chat:
                        services.AddSingleton(Substitute.For<IChatCompletionService>());
                        break;
                    case Mount.S3:
                        services.AddSingleton<IS3AccessKeyStore>(new FakeS3AccessKeyStore());
                        break;
                    case Mount.Feeds:
                        services.AddBifrostFeeds(
                            new FeedOptions { Title = "t", Link = "https://example.test/", Author = "a" },
                            o => o.Endpoint = endpoint);
                        break;
                    case Mount.Prometheus:
                        // The exposition option is non-null by default ("/graphql"); null is still
                        // "the single endpoint" for the mount check, so both refusals apply.
                        services.AddBifrostPrometheus(o => o.Exposition = new PrometheusExpositionOptions { Endpoint = endpoint! });
                        break;
                    case Mount.Pgwire:
                        services.AddSingleton<IPgCredentialStore>(new FakePgCredentialStore());
                        services.AddBifrostPgwire(o =>
                        {
                            o.Endpoint = endpoint;
                            o.ServerCertificate = LdapTestCertificate.Instance;
                        });
                        break;
                    case Mount.Resp:
                        services.AddSingleton<IRespCredentialStore>(new FakeRespCredentialStore());
                        services.AddBifrostResp(o =>
                        {
                            o.Endpoint = endpoint;
                            o.RequireAuthentication = false;
                        });
                        break;
                    case Mount.Ldap:
                        services.AddBifrostLdap(o => o.Endpoint = endpoint);
                        break;
                }
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
                    case Mount.Chat:
                        app.UseBifrostChat(o => o.GraphQlEndpoint = endpoint);
                        break;
                    case Mount.S3:
                        app.UseBifrostS3();
                        break;
                    case Mount.OData:
                        app.UseBifrostOData();
                        break;
                    case Mount.Feeds:
                        app.UseBifrostFeeds();
                        break;
                    case Mount.Prometheus:
                        app.UseBifrostPrometheus();
                        break;
                }
            });
        }).StartAsync();

    [Theory]
    [InlineData(Mount.Blob, "*BifrostBlobOptions.Endpoint*")]
    [InlineData(Mount.AppMetadata, "*GraphQlEndpoint*")]
    [InlineData(Mount.Binary, "*graphqlPath*")]
    [InlineData(Mount.Chat, "*BifrostChatOptions.GraphQlEndpoint*")]
    [InlineData(Mount.S3, "*S3Options.Endpoint*")]
    [InlineData(Mount.OData, "*ODataOptions.Endpoint*")]
    [InlineData(Mount.Feeds, "*FeedEndpointOptions.Endpoint*")]
    [InlineData(Mount.Prometheus, "*PrometheusExpositionOptions.Endpoint*")]
    [InlineData(Mount.Pgwire, "*PgWireOptions.Endpoint*")]
    [InlineData(Mount.Resp, "*RespWireOptions.Endpoint*")]
    [InlineData(Mount.Ldap, "*LdapWireOptions.Endpoint*")]
    public async Task Mount_RefusesToStart_WhenSeveralEndpointsAreRegisteredAndNoneIsNamed(Mount mount, string message)
    {
        var start = () => StartAsync(mount, null, "/graphql", "/other");

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage(message);
    }

    [Theory]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Binary)]
    [InlineData(Mount.Chat)]
    [InlineData(Mount.S3)]
    [InlineData(Mount.OData)]
    [InlineData(Mount.Feeds)]
    [InlineData(Mount.Prometheus)]
    [InlineData(Mount.Pgwire)]
    [InlineData(Mount.Resp)]
    [InlineData(Mount.Ldap)]
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
    [InlineData(Mount.Chat)]
    [InlineData(Mount.S3)]
    [InlineData(Mount.OData)]
    [InlineData(Mount.Feeds)]
    [InlineData(Mount.Prometheus)]
    [InlineData(Mount.Pgwire)]
    [InlineData(Mount.Resp)]
    [InlineData(Mount.Ldap)]
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
    [InlineData(Mount.Chat)]
    [InlineData(Mount.S3)]
    [InlineData(Mount.OData)]
    [InlineData(Mount.Feeds)]
    [InlineData(Mount.Prometheus)]
    [InlineData(Mount.Pgwire)]
    [InlineData(Mount.Resp)]
    [InlineData(Mount.Ldap)]
    public async Task Mount_Starts_WhenNoneIsNamed_OnASingleEndpointHost(Mount mount)
    {
        // Null still selects the single registered endpoint (the binary mount's default).
        _host = await StartAsync(mount, null, "/graphql");

        _host.Should().NotBeNull();
    }
}

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Model;
using BifrostQL.Core.SavedObjects;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The <c>/_app-metadata</c>, <c>/_blob</c> and <c>/_saved-objects</c> mounts take their auth
/// requirement from <c>MountAuthRequirement</c> (protocol-adapter-security, HTTP mounts), the one
/// derivation the frontend, binary and GraphQL mounts already use, and fail closed when the
/// served endpoint cannot be identified. The mount-order facts pin invariant 16: mounted ahead
/// of <c>UseBifrostEndpoints()</c> (which adds the authentication middleware) the mount refuses
/// even a real identity; mounted after it, it serves that identity.
/// </summary>
public sealed class HttpMountAuthRequirementTests : IAsyncLifetime
{
    public enum Mount { AppMetadata, Blob, SavedObjects }

    private const string UserHeader = "X-Test-User";
    private const string TestScheme = "HeaderTest";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mount-auth-{Guid.NewGuid():N}.db");
    private readonly string _savedObjectsDir = Path.Combine(Path.GetTempPath(), $"mount-auth-so-{Guid.NewGuid():N}");
    private IHost? _host;

    public Task InitializeAsync()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE files (id INTEGER PRIMARY KEY, data BLOB);" +
                          "INSERT INTO files (id, data) VALUES (1, x'0102030405');";
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
        if (Directory.Exists(_savedObjectsDir)) Directory.Delete(_savedObjectsDir, recursive: true);
    }

    private static string PathOf(Mount mount) => mount switch
    {
        Mount.AppMetadata => "/_app-metadata",
        Mount.Blob => "/_blob/files/data?k.id=1",
        Mount.SavedObjects => "/_saved-objects",
        _ => throw new ArgumentOutOfRangeException(nameof(mount)),
    };

    private static void UseMount(IApplicationBuilder app, Mount mount)
    {
        switch (mount)
        {
            case Mount.AppMetadata: app.UseBifrostAppMetadata(); break;
            case Mount.Blob: app.UseBifrostBlobs(); break;
            case Mount.SavedObjects: app.UseBifrostSavedObjects(); break;
        }
    }

    /// <param name="mount">The mount under test (no RequireAuth override).</param>
    /// <param name="endpointDisableAuth">The registered endpoint's DisableAuth, or null to register no endpoint.</param>
    /// <param name="mountAfterEndpoints">Mount after (true) or before (false) UseBifrostEndpoints().</param>
    private async Task<HttpClient> StartAsync(Mount mount, bool? endpointDisableAuth, bool mountAfterEndpoints = true)
    {
        var jwt = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Authority"] = "https://login.example.test",
                ["JwtSettings:ClientId"] = "test-client",
            })
            .Build();

        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddBifrostAppMetadata(new IAppMetadataSource[] { new EmptySource() });
                services.AddBifrostSavedObjects(new FileSavedObjectStore(_savedObjectsDir));
                if (endpointDisableAuth is { } disableAuth)
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.BindJwtSettings(jwt.GetSection("JwtSettings"));
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = $"Data Source={_dbPath}";
                            e.Provider = "sqlite";
                            e.Path = "/graphql";
                            e.PlaygroundPath = "/graphiql";
                            e.DisableAuth = disableAuth;
                        });
                    });
                    // The deployment's authentication: UseAuthentication (added by
                    // UseBifrostEndpoints when an endpoint requires auth) authenticates this
                    // scheme, so only middleware placed AFTER it sees the caller.
                    services.AddAuthentication(o =>
                        {
                            o.DefaultAuthenticateScheme = TestScheme;
                            o.DefaultScheme = TestScheme;
                        })
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(TestScheme, _ => { });
                }
            });
            web.Configure(app =>
            {
                if (endpointDisableAuth is null)
                {
                    UseMount(app, mount);
                    return;
                }
                if (!mountAfterEndpoints)
                    UseMount(app, mount);
                app.UseBifrostEndpoints();
                if (mountAfterEndpoints)
                    UseMount(app, mount);
            });
        });
        _host = await builder.StartAsync();
        return _host.GetTestClient();
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, Mount mount, string? user = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PathOf(mount));
        if (user is not null)
            request.Headers.Add(UserHeader, user);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Theory]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.SavedObjects)]
    public async Task Mount_InheritsAnAuthRequiringEndpoint_AndRefusesAnonymousCallers(Mount mount)
    {
        var client = await StartAsync(mount, endpointDisableAuth: false);

        (await GetAsync(client, mount)).Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync(client, mount, user: "alice")).Should().Be(HttpStatusCode.OK,
            "control: the gate is the only thing refusing the anonymous call");
    }

    [Theory]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.SavedObjects)]
    public async Task Mount_InheritsAnAnonymousEndpoint_AndServesAnonymousCallers(Mount mount)
    {
        var client = await StartAsync(mount, endpointDisableAuth: true);

        (await GetAsync(client, mount)).Should().Be(HttpStatusCode.OK,
            "the mount serves the same surface as an endpoint that set DisableAuth, so it carries that posture");
    }

    [Theory]
    [InlineData(Mount.AppMetadata)]
    [InlineData(Mount.Blob)]
    [InlineData(Mount.SavedObjects)]
    public async Task Mount_WithNoIdentifiableEndpoint_FailsClosed(Mount mount)
    {
        var client = await StartAsync(mount, endpointDisableAuth: null);

        (await GetAsync(client, mount)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(Mount.AppMetadata, true)]
    [InlineData(Mount.AppMetadata, false)]
    [InlineData(Mount.Blob, true)]
    [InlineData(Mount.Blob, false)]
    [InlineData(Mount.SavedObjects, true)]
    [InlineData(Mount.SavedObjects, false)]
    public async Task MountOrder_MisorderRefusesEvenARealIdentity_CorrectOrderServesIt(Mount mount, bool mountAfterEndpoints)
    {
        var client = await StartAsync(mount, endpointDisableAuth: false, mountAfterEndpoints);

        var status = await GetAsync(client, mount, user: "alice");

        status.Should().Be(mountAfterEndpoints ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
            mountAfterEndpoints
                ? "after the authentication middleware the mount sees the caller's identity"
                : "ahead of the authentication middleware every caller is anonymous, and the mount must refuse, never serve");
        (await GetAsync(client, mount)).Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous caller is refused in either order");
    }

    private sealed class EmptySource : IAppMetadataSource
    {
        public int Priority => 0;
        public Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
            => Task.FromResult<IDictionary<string, EntityMetadata>>(new Dictionary<string, EntityMetadata>());
    }

    /// <summary>Authenticates any request carrying <see cref="UserHeader"/> as that user.</summary>
    private sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, user) }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

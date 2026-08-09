using System.Net;
using System.Security.Claims;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Security-conformance facts for the <c>/_app-metadata</c> overlay endpoint, equivalent to
/// <c>ProtocolAdapterConformanceTests</c>. Until this session the endpoint was unauthenticated
/// and unfiltered by default while enumerating entities, fields and relationships — an
/// introspection surface with no gate — and it was outside the kit, so nothing caught it.
///
/// <para><b>Why this is not a derivation of the kit</b>: the endpoint serves ONE fixed document
/// on GET. It cannot read a caller-chosen table, take a filter, or write anything, so the kit's
/// read requests, its mutation facts, and its SQL-level assertions have no counterpart. The kit
/// claim that DOES apply to an introspection surface is invariant 4's — the metadata is filtered
/// by the same authorization as the data path, fail-closed — and that is pinned end to end over a
/// real seeded model in <see cref="AppMetadataVisibilityTests"/>.</para>
///
/// <para><b>What this class adds</b> are the two gate facts that class does not reach: the
/// configuration where no Bifrost data path exists (so the overlay is served AS AUTHORED and
/// <c>RequireAuth</c> is the only thing standing in front of it), and an identity the shared
/// <see cref="IBifrostAuthContextFactory"/> cannot project.</para>
/// </summary>
public sealed class AppMetadataConformanceTests : IAsyncLifetime
{
    private readonly string _connString =
        $"Data Source=appmeta_conf_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connString);
        await _keepAlive.OpenAsync();
        await using var cmd = new SqliteCommand(
            "CREATE TABLE members (id INTEGER PRIMARY KEY, first_name TEXT)", _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _keepAlive.DisposeAsync();
    }

    private static AppMetadataModel Overlay() => new()
    {
        Entities = new Dictionary<string, EntityMetadata>
        {
            ["main.members"] = new EntityMetadata { Label = "Members" },
        },
    };

    [Fact]
    public async Task With_no_bifrost_data_path_registered_an_anonymous_caller_is_still_refused()
    {
        // With no IQueryIntentExecutor there is no model to authorize against, so the overlay is
        // served AS AUTHORED — RequireAuth is the sole guard in that configuration, which is
        // exactly why it defaults on. RequireAuth is left UNSET here so the fact proves the
        // DEFAULT rather than a value the fixture supplied.
        var client = await StartAsync(registerDataPath: false);

        using var anonymous = await client.GetAsync("/_app-metadata");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // …and the authenticated call proves the guard is the only thing between an anonymous
        // caller and the whole overlay, so the fact above cannot pass because nothing is served.
        using var authenticated = await client.SendAsync(Authenticated("/_app-metadata"));
        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await authenticated.Content.ReadAsStringAsync()).Should().Contain("main.members");
    }

    [Fact]
    public async Task An_identity_the_shared_factory_cannot_project_never_receives_the_overlay()
    {
        // A token from an OIDC issuer this deployment never mapped. IsAuthenticated is true, so
        // the gate lets it past — the projection is what must stop it, and no overlay may reach
        // the caller.
        var client = await StartAsync(registerDataPath: true, authFactory: new ThrowingAuthFactory());

        var body = await SafeGetAsync(client, Authenticated("/_app-metadata"));

        body.Should().NotContain("main.members",
            "an identity the shared seam cannot project must never be served the overlay");
    }

    [Fact]
    public async Task An_identity_the_shared_factory_cannot_project_is_refused_with_401_not_a_host_fault()
    {
        // Serving nothing is necessary but not sufficient. This endpoint used to satisfy the fact
        // above by letting the projection fault ESCAPE the middleware to the host: a 500, and a
        // stack trace wherever a developer exception page is enabled — invariant 1's shape, an
        // unhandled fault reaching the host on an identity-dependent path. The sibling
        // /_saved-objects gate catches the identical fault and answers 401; both now go through
        // ONE shared gate, so the two cannot drift apart again.
        var client = await StartAsync(registerDataPath: true, authFactory: new ThrowingAuthFactory());

        using var response = await client.SendAsync(Authenticated("/_app-metadata"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unprojectable identity is refused at the gate, not escalated into a host fault");
    }

    // ---- helpers ---------------------------------------------------------

    private const string UserHeader = "X-Test-User";

    private static HttpRequestMessage Authenticated(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(UserHeader, "alice");
        return request;
    }

    /// <summary>
    /// Sends the request and returns the response body, or the empty string when the request
    /// faulted server-side — a fault is a valid way to serve nothing, and the fact under test is
    /// about what the caller RECEIVES.
    /// </summary>
    private static async Task<string> SafeGetAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            using var response = await client.SendAsync(request);
            return response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadAsStringAsync()
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private async Task<HttpClient> StartAsync(
        bool registerDataPath, IBifrostAuthContextFactory? authFactory = null)
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddBifrostAppMetadata(new IAppMetadataSource[] { new StaticSource(Overlay()) });
                if (authFactory != null)
                    services.AddSingleton(authFactory);
                if (registerDataPath)
                    services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
                    {
                        e.ConnectionString = _connString;
                        e.Provider = "sqlite";
                        e.Path = "/graphql";
                        e.DisableAuth = true;
                    }));
            });
            web.Configure(app =>
            {
                // Stands in for the deployment's authentication middleware: any request carrying
                // the marker header arrives authenticated, and nothing else does.
                app.Use(async (ctx, next) =>
                {
                    var user = ctx.Request.Headers[UserHeader].ToString();
                    if (!string.IsNullOrEmpty(user))
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                            new[] { new Claim(ClaimTypes.NameIdentifier, user) }, "test"));
                    await next();
                });
                // RequireAuth is deliberately NOT configured — these facts prove the default.
                app.UseBifrostAppMetadata();
                if (registerDataPath)
                    app.UseBifrostEndpoints();
            });
        });
        _host = await builder.StartAsync();
        return _host.GetTestClient();
    }

    private sealed class StaticSource : IAppMetadataSource
    {
        private readonly AppMetadataModel _model;
        public StaticSource(AppMetadataModel model) => _model = model;
        public int Priority => 0;
        public Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
            => Task.FromResult<IDictionary<string, EntityMetadata>>(
                _model.Entities.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private sealed class ThrowingAuthFactory : IBifrostAuthContextFactory
    {
        public IDictionary<string, object?> CreateUserContext(HttpContext context)
            => throw new InvalidOperationException("unmapped issuer");

        public IDictionary<string, object?> CreateUserContext(
            HttpContext context, IDictionary<string, object?> existing)
            => throw new InvalidOperationException("unmapped issuer");
    }
}

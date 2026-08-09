using System.Net;
using System.Security.Claims;
using System.Text;
using BifrostQL.Core.SavedObjects;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Security-conformance facts for the <c>/_saved-objects</c> CRUD endpoint, equivalent to
/// <c>ProtocolAdapterConformanceTests</c>. Until this session the endpoint shipped anonymous
/// PUT and DELETE by DEFAULT — precisely the class of hole the kit exists to catch — and it was
/// outside the kit, so nothing caught it.
///
/// <para><b>Why this is not a derivation of the kit</b>: the kit's requests are reads and writes
/// of arbitrary TABLES and COLUMNS through the SQL data path, and its facts assert what the
/// transformer pipeline does to them (tenant WHERE, soft-delete rewrite, policy read guard,
/// parameterized SQL). This endpoint is fixed-shape REST over <see cref="ISavedObjectStore"/> —
/// a file- or DB-backed document store that is not the SQL data path at all — so those requests
/// are untranslatable and those facts have no counterpart. The kit's remaining claims, the ones
/// that ARE about the transport gate rather than the query path, are asserted here directly:
/// anonymous refused by default on EVERY verb and path shape, and identity projected through the
/// shared <see cref="IBifrostAuthContextFactory"/> fail-closed in both of its failure modes.</para>
///
/// <para><b>Not proven, because the surface does not have it</b>: per-caller isolation. The store
/// carries no owner/tenant dimension, so any accepted caller can read, overwrite and delete every
/// other caller's saved objects — the kit's cross-tenant facts have no counterpart to assert.
/// That is a property of the store's design, not something these tests can pin; it is recorded
/// here so the absence is visible rather than mistaken for coverage.</para>
/// </summary>
public sealed class SavedObjectsConformanceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"bifrost-so-conformance-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly string PutBody =
        """{"id":"q1","type":"query","name":"probe","definition":{},"version":0}""";

    /// <summary>Every route the endpoint answers, so no verb or path shape is left ungated.</summary>
    public static TheoryData<string, string> EveryRoute() => new()
    {
        { "GET", "/_saved-objects" },
        { "GET", "/_saved-objects?type=query" },
        { "GET", "/_saved-objects/query" },
        { "GET", "/_saved-objects/query/q1" },
        { "PUT", "/_saved-objects/query/q1" },
        { "DELETE", "/_saved-objects/query/q1" },
        { "POST", "/_saved-objects/query/q1" },
    };

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task Every_route_refuses_an_anonymous_caller_by_default(string method, string path)
    {
        // RequireAuth is left UNSET so this exercises the real DEFAULT, not a value the fixture
        // supplied — a fixture that always sets it cannot prove what the default is.
        var client = await BuildClientAsync(configure: null);

        using var response = await client.SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "{0} {1} must be closed to an anonymous caller by default", method, path);
    }

    [Fact]
    public async Task An_identity_the_shared_factory_cannot_project_is_refused()
    {
        // The other half of fail-closed. An empty projection is already pinned by
        // SavedObjectsEndpointTests; a projection that THROWS — a token from an OIDC issuer this
        // deployment never mapped — must be refused too, not escape the gate as an unhandled
        // fault or, worse, fall through as authorized.
        var client = await BuildClientAsync(
            configure: null, user: Authenticated(), authFactory: new ThrowingAuthFactory());

        using var response = await client.SendAsync(Request("PUT", "/_saved-objects/query/q1"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(Path.Combine(_dir, "query", "q1.json")).Should().BeFalse(
            "a refused write must never reach the store");
    }

    [Fact]
    public async Task The_gate_consults_the_host_registered_factory_not_a_private_projection()
    {
        // Proves the gate really goes through the SHARED seam: swapping the registered factory for
        // one that projects nothing changes the answer for a principal the default factory accepts.
        var accepted = await BuildClientAsync(configure: null, user: Authenticated());
        var refused = await BuildClientAsync(
            configure: null, user: Authenticated(), authFactory: new EmptyProjectionAuthFactory());

        using var withDefaultFactory = await accepted.SendAsync(Request("PUT", "/_saved-objects/query/q1"));
        using var withEmptyFactory = await refused.SendAsync(Request("PUT", "/_saved-objects/query/q1"));

        withDefaultFactory.StatusCode.Should().Be(HttpStatusCode.OK);
        withEmptyFactory.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- helpers ---------------------------------------------------------

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST")
            request.Content = new StringContent(PutBody, Encoding.UTF8, "application/json");
        return request;
    }

    private static ClaimsPrincipal Authenticated(string subject = "alice")
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, subject) }, "test"));

    private async Task<HttpClient> BuildClientAsync(
        Action<BifrostSavedObjectsOptions>? configure = null,
        ClaimsPrincipal? user = null,
        IBifrostAuthContextFactory? authFactory = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddBifrostSavedObjects(new FileSavedObjectStore(_dir));
                if (authFactory != null)
                    services.AddSingleton(authFactory);
            });
            web.Configure(app =>
            {
                if (user != null)
                    app.Use(async (ctx, next) => { ctx.User = user; await next(); });
                app.UseBifrostSavedObjects(configure);
            });
        });
        var host = await builder.StartAsync();
        return host.GetTestClient();
    }

    private sealed class EmptyProjectionAuthFactory : IBifrostAuthContextFactory
    {
        public IDictionary<string, object?> CreateUserContext(HttpContext context)
            => new Dictionary<string, object?>();

        public IDictionary<string, object?> CreateUserContext(
            HttpContext context, IDictionary<string, object?> existing)
            => new Dictionary<string, object?>();
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

using System.Net;
using System.Security.Claims;
using System.Text;
using BifrostQL.Core.SavedObjects;
using BifrostQL.Server;
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
/// End-to-end coverage for the <c>/_saved-objects</c> CRUD endpoint over a real
/// TestServer pipeline: create/fetch/list/delete round-trip, the stale-version 409,
/// the validation 400, and the not-found 404. Backed by a file store in a temp dir.
/// </summary>
public sealed class SavedObjectsEndpointTests : IDisposable
{
    private readonly string _dir;

    public SavedObjectsEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bifrost-so-endpoint-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// The CRUD tests below are about CRUD, not auth, so they take the EXPLICIT anonymous
    /// opt-in. That opt-in used to be the default, which is what made every one of them
    /// vacuous as evidence that the endpoint was safe to expose.
    /// </summary>
    private Task<HttpClient> BuildClientAsync()
        => BuildClientAsync(o => o.RequireAuth = false);

    private async Task<HttpClient> BuildClientAsync(
        Action<BifrostSavedObjectsOptions>? configure = null,
        ClaimsPrincipal? user = null,
        IBifrostAuthContextFactory? authFactory = null,
        ISavedObjectStore? store = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddBifrostSavedObjects(store ?? new FileSavedObjectStore(_dir));
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

    private static ClaimsPrincipal Authenticated(string subject = "alice")
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, subject) }, "test"));

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Crud_RoundTrips_ThroughEndpoint()
    {
        var client = await BuildClientAsync();

        // Create (version 0 -> 1).
        var create = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"q1","type":"query","name":"Sales","definition":{"groupBy":["region"]},"version":0}"""));
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        (await create.Content.ReadAsStringAsync()).Should().Contain("\"version\":1");

        // Fetch one.
        var get = await client.GetAsync("/_saved-objects/query/q1");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).Should().Contain("Sales");

        // List the type.
        var list = await client.GetAsync("/_saved-objects/query");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).Should().Contain("q1");

        // Delete -> 204, then gone -> 404.
        (await client.DeleteAsync("/_saved-objects/query/q1")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/_saved-objects/query/q1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StaleVersionPut_Returns409()
    {
        var client = await BuildClientAsync();
        await client.PutAsync("/_saved-objects/form/f1", Body("""{"id":"f1","type":"form","name":"v1","definition":{},"version":0}"""));
        await client.PutAsync("/_saved-objects/form/f1", Body("""{"id":"f1","type":"form","name":"v2","definition":{},"version":1}"""));

        var stale = await client.PutAsync("/_saved-objects/form/f1",
            Body("""{"id":"f1","type":"form","name":"stale","definition":{},"version":1}"""));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NumericType_IsRejected_AndNothingIsPersisted()
    {
        var client = await BuildClientAsync();

        // 999 is no SavedObjectType. Enum.TryParse and the integer-tolerant enum converter both
        // accepted it, persisting under saved-objects/<owner>/999/, a directory ListAsync never
        // enumerates: invisible, undeletable through listing, unbounded.
        var resp = await client.PutAsync("/_saved-objects/999/x",
            Body("""{"id":"x","type":999,"name":"n","definition":{},"version":0}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.Exists(_dir).Should().BeFalse("a refused type writes nothing to disk");
    }

    [Fact]
    public async Task OversizedBody_IsRefusedWith413_CountingReceivedBytes()
    {
        var client = await BuildClientAsync(o =>
        {
            o.RequireAuth = false;
            o.MaxRequestBodyBytes = 1024;
        });
        var json = $$"""{"id":"q1","type":"query","name":"n","definition":{"pad":"{{new string('x', 4096)}}"},"version":0}""";
        // Chunked, no Content-Length: the cap must count what arrives, not what is declared.
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/_saved-objects/query/q1") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var resp = await client.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Directory.Exists(_dir).Should().BeFalse();
    }

    [Fact]
    public async Task CreatesPastThePerOwnerCap_AreRefused_UpdatesAreNot()
    {
        var client = await BuildClientAsync(o => o.RequireAuth = false,
            store: new FileSavedObjectStore(_dir, maxObjectsPerOwner: 2));
        (await client.PutAsync("/_saved-objects/query/a", Body("""{"id":"a","type":"query","name":"a","definition":{},"version":0}""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsync("/_saved-objects/form/b", Body("""{"id":"b","type":"form","name":"b","definition":{},"version":0}""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var third = await client.PutAsync("/_saved-objects/query/c",
            Body("""{"id":"c","type":"query","name":"c","definition":{},"version":0}"""));
        third.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);

        (await client.PutAsync("/_saved-objects/query/a", Body("""{"id":"a","type":"query","name":"a2","definition":{},"version":1}""")))
            .StatusCode.Should().Be(HttpStatusCode.OK, "an update adds no object, so the cap never refuses it");
    }

    [Fact]
    public async Task PutWithBodyPathMismatch_Returns400()
    {
        var client = await BuildClientAsync();
        // Body id ("other") disagrees with the URL id ("q1").
        var resp = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"other","type":"query","name":"x","definition":{},"version":0}"""));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUnknownType_Returns400()
    {
        var client = await BuildClientAsync();
        (await client.GetAsync("/_saved-objects/bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- auth ------------------------------------------------------------
    // The four tests above never touch auth: they were the endpoint's entire coverage
    // while it shipped anonymous PUT/DELETE by default.

    [Fact]
    public async Task ByDefault_AnonymousWritesAreRejected()
    {
        // The default must be closed. Anyone who could reach the port could previously
        // overwrite or delete ANY saved object with no credential at all.
        var client = await BuildClientAsync(configure: null);

        var put = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"q1","type":"query","name":"pwned","definition":{},"version":0}"""));
        put.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.DeleteAsync("/_saved-objects/query/q1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/_saved-objects/query")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ByDefault_AnAuthenticatedCallerIsAllowed()
    {
        var client = await BuildClientAsync(configure: null, user: Authenticated());

        var put = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"q1","type":"query","name":"Sales","definition":{},"version":0}"""));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnonymousWrites_RequireAnExplicitOptIn()
    {
        // The escape hatch still exists — it just has to be chosen.
        var client = await BuildClientAsync(o => o.RequireAuth = false);

        var put = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"q1","type":"query","name":"Sales","definition":{},"version":0}"""));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnAuthenticatedPrincipalThatProjectsToNothing_IsRejected()
    {
        // IsAuthenticated alone is not identity. A principal with no subject, or from an
        // OIDC issuer with no registered claim mapper, projects to an EMPTY Bifrost user
        // context — the shared factory's fail-closed signal. It must be refused here for the
        // same reason every other transport gate refuses it, not waved through because a
        // cookie happened to be present.
        var client = await BuildClientAsync(
            configure: null, user: Authenticated(), authFactory: new EmptyProjectionAuthFactory());

        var put = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"q1","type":"query","name":"pwned","definition":{},"version":0}"""));
        put.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class EmptyProjectionAuthFactory : IBifrostAuthContextFactory
    {
        public IDictionary<string, object?> CreateUserContext(HttpContext context)
            => new Dictionary<string, object?>();

        public IDictionary<string, object?> CreateUserContext(
            HttpContext context, IDictionary<string, object?> existing)
            => new Dictionary<string, object?>();
    }
}

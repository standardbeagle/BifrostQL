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
/// <para><b>Per-caller isolation</b> — the kit's cross-tenant claim — IS asserted here, and used
/// not to be assertable at all: the store carried no owner dimension, so every accepted caller
/// could list, read, overwrite and delete every other caller's objects. Authentication was closed
/// last session; authorization was never there. Each of the four verbs is now pinned separately
/// against TWO callers with distinct projected identities. Two callers is the point: a store that
/// dropped the owner on the floor would pass every single-caller test in this file, so a
/// one-caller fixture is not evidence of isolation, it is evidence of nothing.</para>
/// </summary>
public sealed class SavedObjectsConformanceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"bifrost-so-conformance-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string PutBody(string name = "probe", int version = 0) =>
        $$"""{"id":"q1","type":"query","name":"{{name}}","definition":{},"version":{{version}}}""";

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
        Directory.Exists(_dir).Should().BeFalse("a refused write must never reach the store");
    }

    [Fact]
    public async Task An_identity_that_projects_to_no_stable_owner_key_is_refused()
    {
        // A non-empty projection is not automatically an OWNER. A custom auth factory that
        // projects claims but no canonical user id leaves nothing to partition the store by, and
        // the only safe answer is to refuse — treating it as a shared/global owner would put this
        // caller's objects in the same bucket as every other such caller's.
        var client = await BuildClientAsync(
            configure: null, user: Authenticated(), authFactory: new OwnerlessProjectionAuthFactory());

        using var response = await client.SendAsync(Request("PUT", "/_saved-objects/query/q1"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Directory.Exists(_dir).Should().BeFalse("a refused write must never reach the store");
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

    // ---- per-caller isolation --------------------------------------------
    // Four verbs, four facts, two callers each. Alice always creates first and the fact is stated
    // about what BOB can reach, then about what ALICE still has — the second half is what stops a
    // "deny everything" implementation from passing.

    [Fact]
    public async Task One_callers_objects_are_not_listed_to_another()
    {
        var client = await BuildClientAsync(configure: null);
        (await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", caller: "alice"))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using var bobType = await client.SendAsync(Request("GET", "/_saved-objects/query", caller: "bob"));
        using var bobAll = await client.SendAsync(Request("GET", "/_saved-objects", caller: "bob"));
        using var aliceType = await client.SendAsync(Request("GET", "/_saved-objects/query", caller: "alice"));

        (await bobType.Content.ReadAsStringAsync()).Should().NotContain("q1",
            "a per-type list must not enumerate another caller's objects");
        (await bobAll.Content.ReadAsStringAsync()).Should().NotContain("q1",
            "the unfiltered list must not enumerate another caller's objects either");
        (await aliceType.Content.ReadAsStringAsync()).Should().Contain("q1",
            "the owner still sees her own object — otherwise this fact passes on a broken store");
    }

    [Fact]
    public async Task Reading_another_callers_object_is_indistinguishable_from_reading_a_missing_one()
    {
        var client = await BuildClientAsync(configure: null);
        await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", caller: "alice"));

        using var othersObject = await client.SendAsync(Request("GET", "/_saved-objects/query/q1", caller: "bob"));
        using var noSuchObject = await client.SendAsync(Request("GET", "/_saved-objects/query/nope", caller: "bob"));

        // Same status AND same body: a 404 whose message named the id that exists would still be
        // an existence oracle, letting one caller enumerate another's saved objects by probing.
        othersObject.StatusCode.Should().Be(HttpStatusCode.NotFound);
        noSuchObject.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await othersObject.Content.ReadAsStringAsync())
            .Replace("q1", "ID").Should().Be(
                (await noSuchObject.Content.ReadAsStringAsync()).Replace("nope", "ID"),
                "the two responses may differ only in the id the caller itself supplied");
    }

    [Fact]
    public async Task Writing_to_another_callers_id_does_not_overwrite_it()
    {
        var client = await BuildClientAsync(configure: null);
        await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", caller: "alice", name: "alice-original"));

        // Bob writes the SAME type/id at version 0. In his own partition nothing is there, so this
        // is a create and must succeed: a 409 here would leak that alice holds the id.
        using var bobWrite = await client.SendAsync(
            Request("PUT", "/_saved-objects/query/q1", caller: "bob", name: "bob-clobber"));
        using var aliceRead = await client.SendAsync(Request("GET", "/_saved-objects/query/q1", caller: "alice"));
        using var bobRead = await client.SendAsync(Request("GET", "/_saved-objects/query/q1", caller: "bob"));

        bobWrite.StatusCode.Should().Be(HttpStatusCode.OK,
            "a version conflict on another caller's id would be an existence oracle");
        (await aliceRead.Content.ReadAsStringAsync()).Should().Contain("alice-original")
            .And.NotContain("bob-clobber", "one caller's write must never reach another's object");
        (await bobRead.Content.ReadAsStringAsync()).Should().Contain("bob-clobber",
            "bob's own object is his own, at the same type/id");
    }

    [Fact]
    public async Task Deleting_another_callers_id_does_not_delete_it()
    {
        var client = await BuildClientAsync(configure: null);
        await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", caller: "alice", name: "alice-original"));

        // Delete is a no-op on an absent object, so bob gets the same 204 he would get for an id
        // nobody holds — again, no oracle — and alice's object survives.
        using var bobDelete = await client.SendAsync(Request("DELETE", "/_saved-objects/query/q1", caller: "bob"));
        using var aliceRead = await client.SendAsync(Request("GET", "/_saved-objects/query/q1", caller: "alice"));

        bobDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        aliceRead.StatusCode.Should().Be(HttpStatusCode.OK);
        (await aliceRead.Content.ReadAsStringAsync()).Should().Contain("alice-original");
    }

    [Fact]
    public async Task A_caller_retains_full_control_of_their_own_objects()
    {
        // The positive control for all four facts above: isolation that also broke the owner's own
        // CRUD would satisfy every "bob cannot" assertion while being useless.
        var client = await BuildClientAsync(configure: null);

        (await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", caller: "alice", name: "v1")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", caller: "alice", name: "v2", version: 1)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var read = await client.SendAsync(Request("GET", "/_saved-objects/query/q1", caller: "alice"));
        (await read.Content.ReadAsStringAsync()).Should().Contain("v2");

        (await client.SendAsync(Request("DELETE", "/_saved-objects/query/q1", caller: "alice")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Request("GET", "/_saved-objects/query/q1", caller: "alice")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_auth_disabled_every_caller_shares_one_partition()
    {
        // The documented behaviour of the explicit anonymous opt-in the desktop host takes: no
        // identity means no per-caller partition to derive, so all callers share one owner. This
        // is pinned so it stays a KNOWN property of RequireAuth=false rather than something a
        // reader has to infer, and so it can never leak into the RequireAuth=true path above.
        var client = await BuildClientAsync(o => o.RequireAuth = false);
        await client.SendAsync(Request("PUT", "/_saved-objects/query/q1", name: "shared"));

        using var alsoAnonymous = await client.SendAsync(Request("GET", "/_saved-objects/query/q1"));

        alsoAnonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        (await alsoAnonymous.Content.ReadAsStringAsync()).Should().Contain("shared");
    }

    // ---- helpers ---------------------------------------------------------

    private const string UserHeader = "X-Test-User";

    private static HttpRequestMessage Request(
        string method, string path, string? caller = null, string name = "probe", int version = 0)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (caller != null)
            request.Headers.Add(UserHeader, caller);
        if (method is "PUT" or "POST")
            request.Content = new StringContent(PutBody(name, version), Encoding.UTF8, "application/json");
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
                // Stands in for the deployment's authentication middleware. The marker header
                // selects the caller PER REQUEST, which is what lets one client speak as two
                // distinct identities against one store.
                app.Use(async (ctx, next) =>
                {
                    var header = ctx.Request.Headers[UserHeader].ToString();
                    if (!string.IsNullOrEmpty(header))
                        ctx.User = Authenticated(header);
                    else if (user != null)
                        ctx.User = user;
                    await next();
                });
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

    /// <summary>Projects real claims but no canonical user id — nothing to partition the store by.</summary>
    private sealed class OwnerlessProjectionAuthFactory : IBifrostAuthContextFactory
    {
        private static IDictionary<string, object?> Context()
            => new Dictionary<string, object?> { ["roles"] = new[] { "reader" } };

        public IDictionary<string, object?> CreateUserContext(HttpContext context) => Context();

        public IDictionary<string, object?> CreateUserContext(
            HttpContext context, IDictionary<string, object?> existing) => Context();
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

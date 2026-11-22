using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// What the tenant filter and the actor stamp receive for an HTTP caller when the model
/// configures <c>tenant-context-key</c> / <c>user-audit-key</c>, driven through the production
/// registrar (<c>AddBifrostQL</c>) and the real middleware. The principal lands on the request
/// from a test header (standing in for the host's authentication middleware); a protocol-frontend
/// mount at <see cref="WirePath"/> carries client-supplied wire context, the input a spoof would
/// arrive through.
/// </summary>
public sealed class IdentityContextKeyHttpTests : IAsyncLifetime
{
    private const string ClaimsHeader = "X-Test-Claims";
    private const string WirePath = "/wire";

    private string _dbPath = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"identity-keys-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, org TEXT NULL, body TEXT NOT NULL, created_by TEXT NULL);" +
            "INSERT INTO notes (id, org, body) VALUES (1, 'acme', 'acme-note'), (2, 'globex', 'globex-note');");
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        await Task.CompletedTask;
    }

    // ---- I1: a configured tenant-context-key reads that user-context key, fail closed ----

    [Fact]
    public async Task DefaultTenantKey_ScopesByTheMappedIdentityTenant()
    {
        // Control: with no tenant-context-key the filter reads tenant_id, which the identity
        // mapper writes from the identity's tenant (the local-auth tenant claim).
        await StartAsync("main.notes { tenant-filter: org }");

        using var reply = await SendGraphQlAsync("{ notes { data { body } } }",
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1"), new Claim(Auth.LocalAuthClaims.Tenant, "acme")));

        Errors(reply).Should().BeEmpty();
        Bodies(reply).Should().Equal("acme-note");
    }

    [Fact]
    public async Task ConfiguredTenantKey_IsNotFilledFromTheMappedIdentityTenant_AndDenies()
    {
        // The mapper writes the identity's tenant under tenant_id only; it does not rename it
        // to the model's tenant-context-key. A caller whose tenant arrives that way has no
        // org_id, and the filter refuses rather than reading everyone's rows.
        await StartAsync(
            ":root { tenant-context-key: org_id }",
            "main.notes { tenant-filter: org }");

        using var reply = await SendGraphQlAsync("{ notes { data { body } } }",
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1"), new Claim(Auth.LocalAuthClaims.Tenant, "acme")));

        reply.RootElement.GetRawText().Should().NotContain("-note");
        Errors(reply).Should().ContainSingle().Which.Should().Contain("Tenant context required");
    }

    [Fact]
    public async Task ConfiguredTenantKey_ReadsTheSingleValuedPrincipalClaim()
    {
        // A principal claim whose type is the configured key reaches the user context as a
        // one-element array, which the tenant coercion reads as its single value.
        await StartAsync(
            ":root { tenant-context-key: org_id }",
            "main.notes { tenant-filter: org }");

        using var reply = await SendGraphQlAsync("{ notes { data { body } } }",
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1"), new Claim("org_id", "acme")));

        Errors(reply).Should().BeEmpty();
        Bodies(reply).Should().Equal("acme-note");
    }

    [Fact]
    public async Task ConfiguredTenantKey_MultiValuedPrincipalClaim_Denies()
    {
        await StartAsync(
            ":root { tenant-context-key: org_id }",
            "main.notes { tenant-filter: org }");

        using var reply = await SendGraphQlAsync("{ notes { data { body } } }",
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1"),
                new Claim("org_id", "acme"), new Claim("org_id", "globex")));

        reply.RootElement.GetRawText().Should().NotContain("-note");
        Errors(reply).Should().ContainSingle().Which.Should().Be("Tenant context value is invalid for the target column.");
    }

    // ---- I2: server-owned identity keys can never be supplied by wire context ----

    [Fact]
    public async Task WireContext_CannotSupplyTheConfiguredActorKey()
    {
        // The caller's identity has no `employee_no`, so that slot is absent: a wire entry for
        // the model's user-audit-key must not fill it and author the row.
        await StartAsync(
            ":root { user-audit-key: employee_no }",
            "main.notes.created_by { populate: created-by }");

        using var reply = await SendWireAsync(
            """mutation { notes(insert: { body: "wire-10" }) }""",
            new Dictionary<string, object?> { ["employee_no"] = "spoofed-actor" },
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1")));

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT created_by FROM notes WHERE body = 'wire-10'")).Should().BeNull(
            "the actor comes from the identity projection; a client cannot name it");
    }

    [Theory]
    [InlineData("user_id")]
    [InlineData("id")]
    public async Task WireContext_CannotSupplyTheMapperActorKeys_ForAnAnonymousCaller(string auditKey)
    {
        // An anonymous caller projects to an empty context, so every mapper slot is absent.
        await StartAsync(
            $":root {{ user-audit-key: {auditKey} }}",
            "main.notes.created_by { populate: created-by }");

        using var reply = await SendWireAsync(
            """mutation { notes(insert: { body: "wire-11" }) }""",
            new Dictionary<string, object?> { [auditKey] = "spoofed-actor" },
            principal: null);

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT created_by FROM notes WHERE body = 'wire-11'")).Should().BeNull();
    }

    [Fact]
    public async Task WireContext_CannotSupplyTheConfiguredTenantKey()
    {
        await StartAsync(
            ":root { tenant-context-key: org_id }",
            "main.notes { tenant-filter: org }");

        using var reply = await SendWireAsync(
            "{ notes { data { id body } } }",
            new Dictionary<string, object?> { ["org_id"] = "acme" },
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1")));

        reply.RootElement.GetRawText().Should().NotContain("acme-note");
        Errors(reply).Should().ContainSingle().Which.Should().Contain("Tenant context required");
    }

    [Fact]
    public async Task WireMount_StampsTheIdentityActor()
    {
        // Control for the NULL actors above: through the same wire mount and the same stamp, an
        // identity that carries the actor key gets it recorded, so a NULL there is the strip.
        await StartAsync(
            ":root { user-audit-key: user_id }",
            "main.notes.created_by { populate: created-by }");

        using var reply = await SendWireAsync(
            """mutation { notes(insert: { body: "wire-12" }) }""",
            new Dictionary<string, object?> { ["user_id"] = "spoofed-actor" },
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1")));

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT created_by FROM notes WHERE body = 'wire-12'")).Should().Be("u-1");
    }

    // ---- I4: the actor is a single value, never a raw multi-value claim ----

    [Fact]
    public async Task RawClaimActorKey_IsRefused_NotRecordedAsAnArray()
    {
        // Raw principal claims land in the user context as string[] under their claim type.
        // `sub` is not a key the identity mapper writes, so `user-audit-key: sub` reads that
        // array. It used to reach the created_by parameter (and the state-machine actor as
        // "System.String[]"); the write is refused instead.
        await StartAsync(
            ":root { user-audit-key: sub }",
            "main.notes.created_by { populate: created-by }");

        using var reply = await SendGraphQlAsync(
            """mutation { notes(insert: { body: "raw-sub" }) }""",
            Principal(new Claim("sub", "dana")));

        Errors(reply).Should().ContainSingle().Which.Should().Be(AuditMutationTransformer.NonScalarActorMessage);
        (await ScalarAsync("SELECT count(*) FROM notes WHERE body = 'raw-sub'")).Should().Be(0L);
    }

    [Fact]
    public async Task MapperActorKey_StampsTheSubject()
    {
        // Control: the same principal under the key the mapper writes is stamped as a scalar.
        await StartAsync(
            ":root { user-audit-key: user_id }",
            "main.notes.created_by { populate: created-by }");

        using var reply = await SendGraphQlAsync(
            """mutation { notes(insert: { body: "mapped-sub" }) }""",
            Principal(new Claim("sub", "dana")));

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT created_by FROM notes WHERE body = 'mapped-sub'")).Should().Be("dana");
    }

    // ---- I3: what an anonymous write stamps ----

    [Theory]
    [InlineData("id")]
    [InlineData("user_id")]
    public async Task AnonymousWrite_UnderDisableAuth_StampsANullActor(string auditKey)
    {
        // The shipped Host's Development config (DisableAuth) with its actor key. An
        // unauthenticated caller projects to an EMPTY user context: no "anonymous" id is
        // assigned, so the actor column is stamped NULL. Pinned, not endorsed: whether an
        // anonymous write should be refused instead is a product decision.
        await StartAsync(
            $":root {{ user-audit-key: {auditKey} }}",
            "main.notes.created_by { populate: created-by }");

        using var anonymous = await SendGraphQlAsync(
            """mutation { notes(insert: { body: "anon-write" }) }""", principal: null);
        using var signedIn = await SendGraphQlAsync(
            """mutation { notes(insert: { body: "signed-in-write" }) }""",
            Principal(new Claim(ClaimTypes.NameIdentifier, "u-1")));

        Errors(anonymous).Should().BeEmpty();
        Errors(signedIn).Should().BeEmpty();
        (await ScalarAsync("SELECT count(*) FROM notes WHERE body = 'anon-write' AND created_by IS NULL")).Should().Be(1L);
        (await ScalarAsync("SELECT created_by FROM notes WHERE body = 'signed-in-write'")).Should().Be("u-1",
            "control: the same stamp records an authenticated caller");
    }

    // ---- host ----

    private async Task StartAsync(params string[] metadata)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
        };
        for (var i = 0; i < metadata.Length; i++)
            settings[$"Bifrost:Metadata:{i}"] = metadata[i];
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite"));
                services.AddBifrostEngine();
            });
            web.Configure(app =>
            {
                // Stands in for the host's authentication middleware: lands a principal on the
                // request and nothing more. No header = an unauthenticated caller.
                app.Use((context, next) =>
                {
                    var header = context.Request.Headers[ClaimsHeader].ToString();
                    if (!string.IsNullOrEmpty(header))
                        context.User = DecodePrincipal(header);
                    return next(context);
                });
                app.UseBifrostQL();
                app.UseBifrostFrontend(WirePath, new WireContextFrontend(), requireAuthentication: false);
            });
        }).StartAsync();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "test"));

    private async Task<JsonDocument> SendGraphQlAsync(string query, ClaimsPrincipal? principal)
        => await PostAsync("/graphql", JsonSerializer.Serialize(new { query }), principal);

    private async Task<JsonDocument> SendWireAsync(
        string query, IDictionary<string, object?> wireContext, ClaimsPrincipal? principal)
        => await PostAsync(WirePath, JsonSerializer.Serialize(new { query, context = wireContext }), principal);

    private async Task<JsonDocument> PostAsync(string path, string body, ClaimsPrincipal? principal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (principal is not null)
            request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray())));
        using var response = await _host!.GetTestClient().SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static ClaimsPrincipal DecodePrincipal(string header)
        => Principal(JsonSerializer.Deserialize<string[][]>(Convert.FromBase64String(header))!
            .Select(pair => new Claim(pair[0], pair[1])).ToArray());

    private static IReadOnlyList<string> Bodies(JsonDocument reply)
        => reply.RootElement.GetProperty("data").GetProperty("notes").GetProperty("data")
            .EnumerateArray().Select(row => row.GetProperty("body").GetString()!).ToList();

    private static IReadOnlyList<string> Errors(JsonDocument reply)
        => reply.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateArray().Select(e => e.GetProperty("message").GetString()!).ToList()
            : Array.Empty<string>();

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// A host-written frontend that lets the client send context entries on the wire
    /// (<c>{"query": ..., "context": {...}}</c>) — the input <c>WireContextMerger</c> polices.
    /// </summary>
    private sealed class WireContextFrontend : IProtocolFrontend
    {
        public string ProtocolName => "wire-context";
        public string ContentType => "application/json";
        public string ResponseContentType => "application/json";

        private readonly GraphQLFrontend _graphQl = new(new GraphQL.SystemTextJson.GraphQLSerializer());

        public async ValueTask<BifrostRequest?> ParseAsync(Stream body, CancellationToken cancellationToken)
        {
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var context = new Dictionary<string, object?>();
            if (doc.RootElement.TryGetProperty("context", out var wire))
                foreach (var entry in wire.EnumerateObject())
                    context[entry.Name] = entry.Value.GetString();
            return new BifrostRequest
            {
                Query = doc.RootElement.GetProperty("query").GetString()!,
                UserContext = context,
                CancellationToken = cancellationToken,
            };
        }

        // The response half is the stock GraphQL frontend's; only the parse adds wire context.
        public ValueTask SerializeAsync(Stream output, BifrostResult result, CancellationToken cancellationToken)
            => _graphQl.SerializeAsync(output, result, cancellationToken);
    }
}

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.SavedObjects;
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
/// Tenant isolation of the tenant-stamping stores (deferred change sets, pending changes) when
/// the tenant arrives the documented way: <c>tenant-context-key: org_id</c> fed by a raw
/// <c>org_id</c> principal claim, which <see cref="BifrostContext"/> lands in the user context as
/// a one-element <c>string[]</c>. Driven through the production registrar and middleware. These
/// stores used to record and compare the tenant as <c>value.ToString()</c>, which is
/// <c>"System.String[]"</c> for every org, so every org shared one tenant.
/// </summary>
public sealed class RawClaimTenantIsolationHttpTests : IAsyncLifetime
{
    private const string ClaimsHeader = "X-Test-Claims";

    private string _dbPath = null!;
    private string _savedObjectsDir = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"raw-claim-tenant-{Guid.NewGuid():N}.db");
        _savedObjectsDir = Path.Combine(Path.GetTempPath(), $"raw-claim-saved-{Guid.NewGuid():N}");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, org TEXT NOT NULL, name TEXT NULL, version INTEGER NULL DEFAULT 1);" +
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, org TEXT NOT NULL, name TEXT NOT NULL);" +
            "CREATE TABLE __history (id INTEGER PRIMARY KEY, entity TEXT NOT NULL, entity_id TEXT NOT NULL, op TEXT NOT NULL, actor TEXT NULL, org TEXT NULL, changed_at TEXT NOT NULL, before TEXT NULL, after TEXT NULL, changed_columns TEXT NULL);" +
            "CREATE TABLE change_sets (id INTEGER PRIMARY KEY, state TEXT NOT NULL, undo_window_expires_at TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, tables TEXT NOT NULL, created_at TEXT NOT NULL, applied_at TEXT NULL, reversed_at TEXT NULL);" +
            "CREATE TABLE change_set_deltas (id INTEGER PRIMARY KEY, change_set_id INTEGER NOT NULL, \"table\" TEXT NOT NULL, pk TEXT NOT NULL, op TEXT NOT NULL, inverse_op TEXT NOT NULL, before_image TEXT NULL, after_image TEXT NULL, created_at TEXT NOT NULL);" +
            "CREATE TABLE __outbox (id INTEGER PRIMARY KEY, aggregate TEXT NOT NULL, op TEXT NOT NULL, payload TEXT NOT NULL, tenant TEXT NULL, created_at TEXT NOT NULL, dispatched_at TEXT NULL, attempts INTEGER NOT NULL DEFAULT 0, dead INTEGER NOT NULL DEFAULT 0, change_set_id INTEGER NULL, state TEXT NULL);" +
            "CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, \"table\" TEXT NOT NULL, op TEXT NOT NULL, intended_payload TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, requester_context TEXT NULL, \"state\" TEXT NOT NULL, approver TEXT NULL, decided_at TEXT NULL, reason TEXT NULL);" +
            "INSERT INTO widgets (id, org, name) VALUES (1, 'acme', 'acme-widget'), (2, 'globex', 'globex-widget');");
        await StartAsync(authContextFactory: null);
    }

    private static readonly string[] Metadata =
    {
        ":root { tenant-context-key: org_id; history-table: main.__history; outbox-table: main.__outbox }",
        "main.widgets { tenant-filter: org; deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled; hold-events: until-approved }",
        "main.orders { tenant-filter: org; approval: enabled; approver-role: manager }",
    };

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_savedObjectsDir)) Directory.Delete(_savedObjectsDir, recursive: true);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SavedObject_IsPartitionedByTheOrg_WhenTwoOrgsShareASubject()
    {
        // Same subject from two orgs' IdPs. The owner partition used to read the fixed tenant_id
        // key, absent here, so both orgs hashed to one owner and globex read acme's object.
        using var put = await SendAsync(HttpMethod.Put, "/_saved-objects/query/q1", Member("same-sub", "acme"),
            """{"id":"q1","type":"query","name":"acme-query","definition":{},"version":0}""");
        put.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var globex = await SendAsync(HttpMethod.Get, "/_saved-objects/query/q1", Member("same-sub", "globex"));
        using var acme = await SendAsync(HttpMethod.Get, "/_saved-objects/query/q1", Member("same-sub", "acme"));

        globex.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound, "another org's saved object is not in its partition");
        acme.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "control: the owning org reads it");
        (await acme.Content.ReadAsStringAsync()).Should().Contain("acme-query");
    }

    [Fact]
    public async Task SavedObject_RefusesAMultiValuedOrgClaim_AsAnUnstableIdentity()
    {
        // Two org_id claims name no single tenant to partition by. The tenant read refuses them;
        // the route must answer that as its identity refusal (401), not an unhandled 500.
        var twoOrgs = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "same-sub"), new Claim("org_id", "acme"), new Claim("org_id", "globex"),
        }, authenticationType: "test"));

        using var refused = await SendAsync(HttpMethod.Get, "/_saved-objects", twoOrgs);
        using var control = await SendAsync(HttpMethod.Get, "/_saved-objects", Member("same-sub", "acme"));

        refused.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("acme").And.NotContain("globex");
        control.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "control: a single org claim is served");
    }

    [Fact]
    public async Task DeferredChangeSet_IsStampedWithTheOrg_AndHiddenFromAnotherOrgsReviewQueue()
    {
        var changeSetId = await AcmeEditsItsWidgetAsync();

        using var globex = await SendGraphQlAsync("{ deferredReviewQueue { changeSetId tenant } }", Member("g-1", "globex"));
        using var acme = await SendGraphQlAsync("{ deferredReviewQueue { changeSetId tenant } }", Member("a-2", "acme"));

        Errors(globex).Should().BeEmpty();
        globex.RootElement.GetProperty("data").GetProperty("deferredReviewQueue").GetArrayLength().Should().Be(0,
            "another org's held change set is not in its review queue");
        Errors(acme).Should().BeEmpty();
        acme.RootElement.GetProperty("data").GetProperty("deferredReviewQueue").EnumerateArray()
            .Select(item => item.GetProperty("changeSetId").GetString()).Should().ContainSingle()
                .Which.Should().Be(changeSetId.ToString(), "control: the same org's reviewer sees it");
        (await ScalarAsync($"SELECT tenant FROM change_sets WHERE id = {changeSetId}")).Should().Be("acme");
    }

    [Fact]
    public async Task DeferredChangeSet_CannotBeUndoneByAnotherOrg()
    {
        var changeSetId = await AcmeEditsItsWidgetAsync();

        using var globex = await SendGraphQlAsync(
            $"mutation {{ undo(changeSetId: {changeSetId}) {{ undoneRows conflictRows }} }}", Member("g-1", "globex"));

        Errors(globex).Should().ContainSingle().Which.Should().Be("The deferred change set was not found.");
        (await ScalarAsync($"SELECT state FROM change_sets WHERE id = {changeSetId}")).Should().Be("held");

        using var acme = await SendGraphQlAsync(
            $"mutation {{ undo(changeSetId: {changeSetId}) {{ undoneRows conflictRows }} }}", Member("a-1", "acme"));
        Errors(acme).Should().BeEmpty("control: the owning org can undo it");
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 1")).Should().Be("acme-widget");
    }

    [Fact]
    public async Task PendingChange_IsStampedWithTheOrg_AndHiddenFromAnotherOrgsReviewer()
    {
        using var write = await SendGraphQlAsync(
            """mutation { orders(insert: { name: "acme-order" }) }""", Member("a-1", "acme"));
        (await ScalarAsync("SELECT count(*) FROM pending_changes")).Should().Be(1L, Errors(write).FirstOrDefault());

        using var globex = await SendGraphQlAsync("{ pending_changes { data { id } } }", Member("g-1", "globex", "manager"));
        using var acme = await SendGraphQlAsync("{ pending_changes { data { id } } }", Member("a-2", "acme", "manager"));

        Errors(globex).Should().BeEmpty();
        PendingIds(globex).Should().BeEmpty("another org's reviewer does not see the queued change");
        Errors(acme).Should().BeEmpty();
        PendingIds(acme).Should().HaveCount(1, "control: the same org's reviewer sees it");
        (await ScalarAsync("SELECT tenant FROM pending_changes")).Should().Be("acme");
    }

    [Fact]
    public async Task HistoryTrail_IsScopedToTheRawClaimOrg()
    {
        await ExecAsync(
            "INSERT INTO __history (entity, entity_id, op, org, changed_at) VALUES " +
            "('main.widgets', '1', 'update', 'acme', '2026-01-01'), ('main.widgets', '2', 'update', 'globex', '2026-01-01');");

        using var acme = await SendGraphQlAsync("{ widgetsHistory { data { entity_id org } } }", Member("a-1", "acme"));

        Errors(acme).Should().BeEmpty();
        acme.RootElement.GetProperty("data").GetProperty("widgetsHistory").GetProperty("data").EnumerateArray()
            .Select(row => row.GetProperty("org").GetString()).Should().Equal("acme");
    }

    [Fact]
    public async Task HistoryTrail_RefusesAMultiValuedOrgClaim()
    {
        await ExecAsync(
            "INSERT INTO __history (entity, entity_id, op, org, changed_at) VALUES " +
            "('main.widgets', '1', 'update', 'acme', '2026-01-01'), ('main.widgets', '2', 'update', 'globex', '2026-01-01');");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "x-1"), new Claim("org_id", "acme"), new Claim("org_id", "globex") },
            authenticationType: "test"));

        using var reply = await SendGraphQlAsync("{ widgetsHistory { data { entity_id org } } }", principal);

        Errors(reply).Should().ContainSingle().Which.Should().Be("Tenant context value is invalid for the target column.");
        reply.RootElement.GetProperty("data").GetProperty("widgetsHistory").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CaseVariantClaimType_ScopesRead_Mutation_AndHistoryAlike()
    {
        // The token says "Org_Id", the model says "org_id". The HTTP user context compares keys
        // case-insensitively, so every surface must find the tenant, not just the ones that read
        // the request context without copying it.
        await ExecAsync(
            "INSERT INTO __history (entity, entity_id, op, org, changed_at) VALUES " +
            "('main.widgets', '1', 'update', 'acme', '2026-01-01'), ('main.widgets', '2', 'update', 'globex', '2026-01-01');");
        var acmeCaseVariant = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "a-1"), new Claim("Org_Id", "acme") }, authenticationType: "test"));

        using var read = await SendGraphQlAsync("{ widgets { data { name } } }", acmeCaseVariant);
        using var history = await SendGraphQlAsync("{ widgetsHistory { data { org } } }", acmeCaseVariant);
        using var write = await SendGraphQlAsync(
            """mutation { widgets(update: { id: 1, name: "acme-edited", version: 1 }) }""", acmeCaseVariant);

        Errors(read).Should().BeEmpty();
        read.RootElement.GetProperty("data").GetProperty("widgets").GetProperty("data").EnumerateArray()
            .Select(row => row.GetProperty("name").GetString()).Should().Equal("acme-widget");
        Errors(history).Should().BeEmpty();
        history.RootElement.GetProperty("data").GetProperty("widgetsHistory").GetProperty("data").EnumerateArray()
            .Select(row => row.GetProperty("org").GetString()).Should().Equal("acme");
        Errors(write).Should().BeEmpty();
        (await ScalarAsync("SELECT tenant FROM change_sets")).Should().Be("acme");
    }

    [Fact]
    public async Task HostFactory_OrdinalCaseVariantKey_ScopesReadAndMutationAlike()
    {
        // A host IBifrostAuthContextFactory returns an ORDINAL dictionary whose tenant key is
        // "Org_Id" while the model says "org_id". The gate normalises it once, so the read path
        // (which copies the context) and the mutation path (which reads it directly) agree.
        await RestartWithFactoryAsync(() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a-1",
            ["Org_Id"] = "acme",
        });

        using var read = await SendGraphQlAsync("{ widgets { data { name } } }", Member("a-1", "ignored"));
        using var write = await SendGraphQlAsync(
            """mutation { widgets(update: { id: 1, name: "acme-edited", version: 1 }) }""", Member("a-1", "ignored"));

        Errors(read).Should().BeEmpty();
        read.RootElement.GetProperty("data").GetProperty("widgets").GetProperty("data").EnumerateArray()
            .Select(row => row.GetProperty("name").GetString()).Should().Equal("acme-widget");
        Errors(write).Should().BeEmpty();
        (await ScalarAsync("SELECT tenant FROM change_sets")).Should().Be("acme");
    }

    [Fact]
    public async Task HostFactory_TwinCaseKeys_AreRefusedAtTheGate()
    {
        await RestartWithFactoryAsync(() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a-1",
            ["org_id"] = "acme",
            ["Org_Id"] = "globex",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = "{ widgets { data { name } } }" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new[] { new[] { ClaimTypes.NameIdentifier, "a-1" } })));
        using var response = await _host!.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden, body);
        body.Should().NotContain("widget");
    }

    private sealed class FixedAuthContextFactory : IBifrostAuthContextFactory
    {
        private readonly Func<IDictionary<string, object?>> _create;
        public FixedAuthContextFactory(Func<IDictionary<string, object?>> create) => _create = create;
        public IDictionary<string, object?> CreateUserContext(Microsoft.AspNetCore.Http.HttpContext context) => _create();
    }

    private async Task RestartWithFactoryAsync(Func<IDictionary<string, object?>> create)
    {
        _host?.Dispose();
        await StartAsync(new FixedAuthContextFactory(create));
    }

    private async Task<long> AcmeEditsItsWidgetAsync()
    {
        using var reply = await SendGraphQlAsync(
            """mutation { widgets(update: { id: 1, name: "acme-edited", version: 1 }) }""", Member("a-1", "acme"));
        Errors(reply).Should().BeEmpty();
        return (long)(await ScalarAsync("SELECT id FROM change_sets"))!;
    }

    // The org arrives as a raw principal claim, the documented tenant-context-key setup.
    private static ClaimsPrincipal Member(string id, string org, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new("org_id", org) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static IReadOnlyList<long> PendingIds(JsonDocument reply)
        => reply.RootElement.GetProperty("data").GetProperty("pending_changes").GetProperty("data")
            .EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToList();

    private async Task StartAsync(IBifrostAuthContextFactory? authContextFactory)
    {
        var metadata = Metadata;
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
                services.AddBifrostSavedObjects(new FileSavedObjectStore(_savedObjectsDir));
                if (authContextFactory is not null)
                    services.AddSingleton(authContextFactory);
            });
            web.Configure(app =>
            {
                // Stands in for the host's authentication middleware: lands a principal on the request.
                app.Use((context, next) =>
                {
                    var header = context.Request.Headers[ClaimsHeader].ToString();
                    if (!string.IsNullOrEmpty(header))
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            JsonSerializer.Deserialize<string[][]>(Convert.FromBase64String(header))!
                                .Select(pair => new Claim(pair[0], pair[1])), authenticationType: "test"));
                    return next(context);
                });
                app.UseBifrostQL();
                app.UseBifrostSavedObjects(o => o.RequireAuth = true);
            });
        }).StartAsync();
    }

    private async Task<JsonDocument> SendGraphQlAsync(string query, ClaimsPrincipal principal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray())));
        using var response = await _host!.GetTestClient().SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ClaimsPrincipal principal, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray())));
        return await _host!.GetTestClient().SendAsync(request);
    }

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
}


using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
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
/// What the state-transition audit row records when a protocol frontend carries client-supplied
/// wire context, driven through the production registrar (<c>AddBifrostQL</c>), the real
/// frontend middleware and the registered <see cref="StateTransitionAuditObserver"/>. The
/// principal lands on the request from a test header, standing in for the host's authentication.
/// </summary>
public sealed class StateTransitionAuditWireContextTests : IAsyncLifetime
{
    private const string ClaimsHeader = "X-Test-Claims";
    private const string WirePath = "/wire";
    private const string Profile = "sm";

    private string _dbPath = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"transition-audit-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await ExecAsync(
            "CREATE TABLE tickets (id INTEGER PRIMARY KEY, org TEXT NULL, status TEXT NOT NULL DEFAULT 'open');" +
            "CREATE TABLE audit_log (audit_id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INTEGER NULL, " +
            "actor_user_id INTEGER NULL, action TEXT NOT NULL, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, " +
            "summary TEXT NULL, created_at TEXT NOT NULL);" +
            "INSERT INTO tickets (id, org, status) VALUES (1, 'acme', 'open');");
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WireTenantId_DoesNotReachTheAuditRow_WhenTenantContextKeyIsConfigured()
    {
        // tenant-context-key renames the key the tenant filter reads, but tenant_id is still the
        // key the identity mapper writes and the audit observer reads. A caller whose identity has
        // no tenant must not be able to author the audit row's tenant from the wire.
        await StartAsync(":root { tenant-context-key: org_id }");

        using var reply = await SendTransitionAsync(
            new Dictionary<string, object?> { ["tenant_id"] = "999" },
            Principal(new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "admin")));

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT count(*) FROM audit_log")).Should().Be(1L,
            "control: the transition is audited, so a NULL tenant below is the strip");
        (await ScalarAsync("SELECT tenant_id FROM audit_log")).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "3")]
    [InlineData(":root { tenant-context-key: org_id }", "5")]
    public async Task AuditTenant_IsReadFromTheModelsTenantContextKey(string? metadata, string expectedTenant)
    {
        // The identity's tenant lands in tenant_id (3); the org_id claim (5) is what the tenant
        // filter reads once tenant-context-key names it. The audit row records the tenant the
        // tenant filter scoped the write by.
        await StartAsync(metadata is null ? [] : [metadata]);

        using var reply = await SendTransitionAsync(
            new Dictionary<string, object?>(),
            Principal(new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "admin"),
                new Claim(Auth.LocalAuthClaims.Tenant, "3"), new Claim("org_id", "5")));

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT tenant_id FROM audit_log")).Should().Be(long.Parse(expectedTenant));
    }

    [Fact]
    public async Task WireUserKeyInAnotherCase_DoesNotSuppressTheAuditRow()
    {
        // The authenticated context carries the raw principal under "user". A wire "User" used to
        // merge beside it; the audit observer's case-insensitive copy then threw on the duplicate
        // key, the observer failure was logged and swallowed, and the transition went unaudited.
        await StartAsync();

        using var reply = await SendTransitionAsync(
            new Dictionary<string, object?> { ["User"] = "spoof" },
            Principal(new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "admin")));

        Errors(reply).Should().BeEmpty();
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 1")).Should().Be("closed");
        (await ScalarAsync("SELECT count(*) FROM audit_log")).Should().Be(1L,
            "a committed transition is always audited");
    }

    // ---- host ----

    private async Task StartAsync(params string[] extraMetadata)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            [$"Profiles:{Profile}:modules:0"] = "state-machine",
        };
        var metadata = extraMetadata.Append(
            "main.tickets { state-column: status; initial-state: open; states: open,closed; " +
            "transitions: open->closed[admin]@ticket.closed }").ToArray();
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
                    .BindProvider("sqlite")
                    .BindProfiles(config.GetSection("Profiles")));
                services.AddBifrostEngine();
            });
            web.Configure(app =>
            {
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

    private async Task<JsonDocument> SendTransitionAsync(
        IDictionary<string, object?> wireContext, ClaimsPrincipal principal)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = """mutation { tickets(update: { id: 1, status: "closed" }) }""",
            context = wireContext,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, WirePath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-BifrostQL-Profile", Profile);
        request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray())));
        using var response = await _host!.GetTestClient().SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static ClaimsPrincipal DecodePrincipal(string header)
        => Principal(JsonSerializer.Deserialize<string[][]>(Convert.FromBase64String(header))!
            .Select(pair => new Claim(pair[0], pair[1])).ToArray());

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

    /// <summary>A host-written frontend that lets the client send context entries on the wire.</summary>
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

        public ValueTask SerializeAsync(Stream output, BifrostResult result, CancellationToken cancellationToken)
            => _graphQl.SerializeAsync(output, result, cancellationToken);
    }
}

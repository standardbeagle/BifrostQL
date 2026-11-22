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
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"raw-claim-tenant-{Guid.NewGuid():N}.db");
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
        await StartAsync(
            ":root { tenant-context-key: org_id; history-table: main.__history; outbox-table: main.__outbox }",
            "main.widgets { tenant-filter: org; deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled; hold-events: until-approved }",
            "main.orders { tenant-filter: org; approval: enabled; approver-role: manager }");
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        await Task.CompletedTask;
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


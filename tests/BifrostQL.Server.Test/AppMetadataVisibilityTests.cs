using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// <c>/_app-metadata</c> is an INTROSPECTION surface — it enumerates entities, fields, grid
    /// columns and relationships — so it is gated like one: authentication by default, and the
    /// served overlay narrowed to what the caller may READ under the same policy evaluator the
    /// query path enforces (.claude/rules/protocol-adapter-security.md invariant 4).
    ///
    /// <para>These facts run over a real seeded SQLite model behind the full endpoint stack, so
    /// the filter is exercised through the actual <c>PolicyConfigCollector</c> /
    /// <c>PolicyEvaluator</c> path rather than a stub. The callers here are DELIBERATELY
    /// anonymous or role-less: an authenticated-admin-only suite cannot manifest this class of
    /// disclosure at all.</para>
    /// </summary>
    public sealed class AppMetadataVisibilityTests : IAsyncLifetime
    {
        private readonly string _connString =
            $"Data Source=appmeta_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private SqliteConnection _keepAlive = null!;
        private IHost? _host;

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            await Exec("CREATE TABLE members (id INTEGER PRIMARY KEY, first_name TEXT, salary REAL)");
            await Exec("CREATE TABLE ledger (id INTEGER PRIMARY KEY, amount REAL)");
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

        // members is readable but hides `salary` from every non-admin caller; ledger denies
        // read outright (an empty action grant with a policy present = deny).
        private static readonly string[] Metadata =
        {
            "main.members { policy-actions: read; policy-read-deny: salary }",
            "main.ledger { policy-actions: update }",
        };

        private static AppMetadataModel Overlay() => new()
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["main.members"] = new EntityMetadata
                {
                    Label = "Members",
                    DisplayFields = new[] { "first_name", "salary" },
                    Fields = new Dictionary<string, FieldMetadata>
                    {
                        ["first_name"] = new FieldMetadata { Widget = "text" },
                        ["salary"] = new FieldMetadata { Widget = "currency" },
                    },
                    Grid = new GridPresetMetadata
                    {
                        DefaultColumns = new[] { "first_name", "salary" },
                        // A direction-suffixed sort on a VISIBLE column must survive; one on a
                        // read-denied column (salary) must be dropped like any other reference.
                        DefaultSort = new[] { "first_name desc", "salary asc" },
                    },
                    Relationships = new Dictionary<string, RelationshipMetadata>
                    {
                        ["entries"] = new RelationshipMetadata { TargetEntity = "main.ledger" },
                    },
                },
                ["main.ledger"] = new EntityMetadata { Label = "Ledger" },
                // An overlay entry for a table this deployment does not expose at all.
                ["main.ghost"] = new EntityMetadata { Label = "Ghost" },
            },
        };

        [Fact]
        public async Task An_anonymous_caller_is_rejected_by_default()
        {
            var client = await StartAsync();

            using var response = await client.GetAsync("/_app-metadata");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the overlay is introspection and must not be world-readable by default");
        }

        [Fact]
        public async Task A_read_denied_entity_is_absent_from_the_served_overlay()
        {
            var client = await StartAsync();

            var overlay = await GetOverlay(client, user: "plain-user");

            overlay.Entities.Should().ContainKey("main.members");
            overlay.Entities.Should().NotContainKey("main.ledger",
                "the caller cannot read the table, so the overlay must not disclose it");
        }

        [Fact]
        public async Task A_read_denied_column_is_absent_from_every_reference()
        {
            var client = await StartAsync();

            var overlay = await GetOverlay(client, user: "plain-user");
            var members = overlay.Entities["main.members"];

            members.Fields.Should().ContainKey("first_name");
            members.Fields.Should().NotContainKey("salary");
            members.DisplayFields.Should().Equal("first_name");
            members.Grid!.DefaultColumns.Should().Equal("first_name");
        }

        [Fact]
        public async Task A_direction_suffixed_default_sort_on_a_visible_column_survives()
        {
            // Regression: the visibility filter tested the whole directive ("first_name desc")
            // as a column name, so HasColumn was always false and EVERY descending/asc-suffixed
            // default sort was stripped for every caller — a functional bug, not a policy one.
            // The sort on the read-denied column must still drop.
            var client = await StartAsync();

            var overlay = await GetOverlay(client, user: "plain-user");
            var grid = overlay.Entities["main.members"].Grid!;

            // A direction-suffixed sort on a visible column is preserved intact, while the
            // sort on the read-denied 'salary' column is dropped.
            grid.DefaultSort.Should().Equal(new[] { "first_name desc" });
        }

        [Fact]
        public async Task A_relationship_to_an_invisible_entity_is_omitted()
        {
            var client = await StartAsync();

            var overlay = await GetOverlay(client, user: "plain-user");

            overlay.Entities["main.members"].Relationships.Should().NotContainKey("entries",
                "advertising a link to a table the caller cannot read is the same disclosure");
        }

        [Fact]
        public async Task An_entity_with_no_matching_table_is_dropped_fail_closed()
        {
            var client = await StartAsync();

            var overlay = await GetOverlay(client, user: "plain-user");

            overlay.Entities.Should().NotContainKey("main.ghost");
        }

        [Fact]
        public async Task An_admin_caller_sees_the_whole_overlay()
        {
            // The filter must be the policy evaluator's answer, not a blanket redaction: the
            // evaluator's admin bypass has to carry through, or the fact above would pass
            // vacuously against a filter that simply drops everything.
            var client = await StartAsync();

            var overlay = await GetOverlay(client, user: "root", role: "admin");

            overlay.Entities.Keys.Should().Contain(new[] { "main.members", "main.ledger" });
            overlay.Entities["main.members"].Fields.Should().ContainKey("salary");
            overlay.Entities["main.members"].Relationships.Should().ContainKey("entries");
        }

        [Fact]
        public async Task An_operator_can_still_opt_into_the_open_anonymous_endpoint()
        {
            var client = await StartAsync(requireAuth: false);

            using var response = await client.GetAsync("/_app-metadata");

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "RequireAuth = false is the explicit opt-in that used to be the default");
        }

        // ---- helpers ----

        private static async Task<AppMetadataModel> GetOverlay(
            HttpClient client, string user, string? role = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/_app-metadata");
            request.Headers.Add(RoleHeaderAuthHandler.UserHeader, user);
            if (role is not null)
                request.Headers.Add(RoleHeaderAuthHandler.RoleHeader, role);

            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return AppMetadataJson.Deserialize(await response.Content.ReadAsStringAsync());
        }

        /// <summary>
        /// Starts the host. <paramref name="requireAuth"/> null leaves the option UNSET, so the
        /// gate facts exercise the real DEFAULT rather than a value the fixture supplied — a
        /// fixture that always sets it cannot prove what the default is.
        /// </summary>
        private async Task<HttpClient> StartAsync(bool? requireAuth = null)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddAuthentication(RoleHeaderAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthHandler>(
                            RoleHeaderAuthHandler.SchemeName, _ => { });
                    services.AddBifrostAppMetadata(new IAppMetadataSource[] { new StaticSource(Overlay()) });
                    services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
                    {
                        e.ConnectionString = _connString;
                        e.Provider = "sqlite";
                        e.Path = "/graphql";
                        e.Metadata = Metadata;
                        e.DisableAuth = true;
                    }));
                });
                web.Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseBifrostAppMetadata(o =>
                    {
                        if (requireAuth is { } value)
                            o.RequireAuth = value;
                    });
                    app.UseBifrostEndpoints();
                });
            });
            _host = await builder.StartAsync();
            return _host.GetTestClient();
        }

        private async Task Exec(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
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
    }

    /// <summary>
    /// Header-driven test authentication carrying a role claim, so the policy evaluator's
    /// admin bypass and role-qualified denies are both reachable from a test request.
    /// </summary>
    internal sealed class RoleHeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "RoleTest";
        public const string UserHeader = "X-Test-User";
        public const string RoleHeader = "X-Test-Role";

        public RoleHeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
            var role = Request.Headers[RoleHeader].ToString();
            if (!string.IsNullOrEmpty(role))
                claims.Add(new Claim(ClaimTypes.Role, role));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}

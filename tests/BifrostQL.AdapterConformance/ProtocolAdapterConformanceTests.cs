using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.AdapterConformance
{
    /// <summary>
    /// A read request the conformance suite hands to an adapter under test. The
    /// derived suite translates it into the adapter's own wire format: the point of
    /// every conformance fact is that the request travels the adapter's real
    /// request path, not a shortcut into core.
    /// </summary>
    public sealed class ConformanceReadRequest
    {
        /// <summary>Database table name (e.g. <c>orders</c>).</summary>
        public required string Table { get; init; }

        /// <summary>Database column names to read.</summary>
        public required IReadOnlyList<string> Columns { get; init; }

        /// <summary>Optional caller filter in GraphQL filter shape (<c>{ name: { _eq: "x" } }</c>).</summary>
        public IReadOnlyDictionary<string, object?>? Filter { get; init; }

        /// <summary>
        /// The caller identity as the adapter's wire would deliver it, or null for
        /// an unauthenticated request. Adapters must project this through
        /// <c>IBifrostAuthContextFactory</c> — never invent their own claim mapping.
        /// </summary>
        public ClaimsPrincipal? Principal { get; init; }

        /// <summary>The registered BifrostQL endpoint path the read targets.</summary>
        public required string Endpoint { get; init; }
    }

    /// <summary>
    /// Reusable security-conformance suite for <see cref="IProtocolAdapter"/>
    /// implementations. Every adapter (RESP, MQTT, pgwire, …) derives from this
    /// class instead of copying these tests; a passing suite proves the adapter is
    /// not a security hole around the GraphQL pipeline:
    ///
    /// <list type="bullet">
    /// <item><b>Security transformers apply</b> — tenant-filter and soft-delete
    /// WHERE clauses are present in the SQL the adapter's reads generate, and
    /// cross-tenant / soft-deleted rows never surface.</item>
    /// <item><b>SQL is parameterized</b> — caller-supplied values bind as
    /// <c>@p</c> parameters and are never inlined into SQL text.</item>
    /// <item><b>Policy read guards hold</b> — a <c>policy-read-deny</c> column is
    /// rejected whether selected or used as a filter oracle, and a missing tenant
    /// identity fails closed.</item>
    /// </list>
    ///
    /// <para><b>How to plug in a new adapter</b>: derive a class in your adapter's
    /// test project, override <see cref="RegisterAdapter"/> to register the adapter
    /// on the endpoint options (typically
    /// <c>options.AddProtocolAdapter&lt;MyAdapter&gt;()</c>), and override
    /// <see cref="ExecuteReadAsync"/> to encode the request in your wire format,
    /// send it through the adapter's real request path, and decode the response
    /// rows. Server-side rejections must surface as a thrown exception carrying the
    /// server's error text (in its message chain) — a suite that swallows errors
    /// cannot prove fail-closed behavior. The base class owns the fixture: a shared
    /// in-memory SQLite database, the security metadata rules, the host, and a SQL
    /// capture observer. See <c>EchoProtocolAdapterConformanceTests</c> in
    /// BifrostQL.Server.Test for the reference derivation.</para>
    /// </summary>
    public abstract class ProtocolAdapterConformanceTests : IAsyncLifetime
    {
        /// <summary>The endpoint path the fixture registers; requests target it.</summary>
        protected const string EndpointPath = "/graphql";

        private readonly string _connString;
        private readonly SqlCaptureObserver _sqlCapture = new();
        private SqliteConnection _keepAlive = null!;

        /// <summary>The started host; derived suites resolve their adapter from its services.</summary>
        protected IHost Host { get; private set; } = null!;

        protected ProtocolAdapterConformanceTests()
        {
            // One in-memory database per derived suite so parallel test classes
            // never share (or clobber) each other's fixture data.
            _connString = $"Data Source=conformance_{GetType().Name};Mode=Memory;Cache=Shared";
        }

        /// <summary>Registers the adapter under test on the endpoint options.</summary>
        protected abstract void RegisterAdapter(BifrostMultiDbOptions options);

        /// <summary>
        /// Executes a read through the adapter's real request path and returns the
        /// decoded rows. Server-side rejections must propagate as exceptions whose
        /// message chain contains the server error text.
        /// </summary>
        protected abstract Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request);

        private static readonly string[] MetadataRules =
        {
            "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at }",
            "*.documents { policy-read-deny: body }",
        };

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            await Exec("DROP TABLE IF EXISTS orders");
            await Exec("DROP TABLE IF EXISTS documents");
            await Exec(
                """
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    deleted_at TEXT NULL
                )
                """);
            await Exec(
                """
                INSERT INTO orders(id, tenant_id, name, deleted_at) VALUES
                    (1, 'tenant-a', 'a-first', NULL),
                    (2, 'tenant-a', 'a-second', NULL),
                    (3, 'tenant-b', 'b-only', NULL),
                    (4, 'tenant-a', 'a-deleted', '2026-01-01T00:00:00Z')
                """);
            await Exec(
                """
                CREATE TABLE documents (
                    id INTEGER PRIMARY KEY,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL
                )
                """);
            await Exec("INSERT INTO documents(id, title, body) VALUES (1, 'public title', 'secret body')");

            Host = await BuildHostAsync();
        }

        public async Task DisposeAsync()
        {
            if (Host is not null)
            {
                await Host.StopAsync();
                Host.Dispose();
            }
            await _keepAlive.DisposeAsync();
        }

        private async Task Exec(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<IHost> BuildHostAsync()
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = MetadataRules;
                            e.DisableAuth = true;
                        });
                        o.AddQueryObservers(new IQueryObserver[] { _sqlCapture });
                        RegisterAdapter(o);
                    });
                });
                web.Configure(_ => { });
            });
            return await builder.StartAsync();
        }

        /// <summary>A caller identity carrying a tenant claim, as a real wire would deliver it.</summary>
        protected static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "test"));

        private static ConformanceReadRequest OrdersRequest(
            ClaimsPrincipal? principal, IReadOnlyDictionary<string, object?>? filter = null) =>
            new()
            {
                Table = "orders",
                Columns = new[] { "id", "tenant_id", "name" },
                Filter = filter,
                Principal = principal,
                Endpoint = EndpointPath,
            };

        /// <summary>
        /// All SQL the adapter's reads generated against a table, captured at the
        /// AfterExecute phase (the phase that carries SQL text). Throws when the
        /// table produced no SQL — a suite asserting on SQL that never ran would
        /// prove nothing.
        /// </summary>
        private string CapturedSql(string table)
        {
            var sql = _sqlCapture.SqlFor(table);
            if (sql.Count == 0)
                throw new InvalidOperationException(
                    $"No SQL was captured for table '{table}'. The adapter's read never reached SQL execution.");
            return string.Join("\n---\n", sql);
        }

        private async Task AssertReadRejectedAsync(ConformanceReadRequest request, string expectedErrorFragment)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteReadAsync(request));
            FlattenMessages(ex).Should().Contain(expectedErrorFragment,
                "the adapter must surface the server-side rejection, not swallow or replace it");
        }

        private static string FlattenMessages(Exception ex)
        {
            var messages = new List<string>();
            for (Exception? current = ex; current is not null; current = current.InnerException)
            {
                messages.Add(current.Message);
                if (current is AggregateException aggregate)
                    messages.AddRange(aggregate.InnerExceptions.Select(FlattenMessages));
            }
            return string.Join(" | ", messages);
        }

        // ---- (a) security transformers apply -------------------------------

        [Fact]
        public async Task Read_TenantPrincipal_SeesOnlyItsOwnTenantRows()
        {
            var tenantARows = await ExecuteReadAsync(OrdersRequest(TenantPrincipal("user-a", "tenant-a")));
            tenantARows.Should().HaveCount(2);
            tenantARows.Select(r => (string)r["name"]!).Should().BeEquivalentTo("a-first", "a-second");

            var tenantBRows = await ExecuteReadAsync(OrdersRequest(TenantPrincipal("user-b", "tenant-b")));
            tenantBRows.Should().ContainSingle(r => (string)r["name"]! == "b-only");
        }

        [Fact]
        public async Task Read_TenantWhereClause_IsPresentInGeneratedSql()
        {
            await ExecuteReadAsync(OrdersRequest(TenantPrincipal("user-a", "tenant-a")));

            // The caller asked for the whole table; the tenant predicate must have
            // been injected by the transformer pipeline, not by the adapter's codec.
            CapturedSql("orders").Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
        }

        [Fact]
        public async Task Read_SoftDeletedRows_NeverSurface()
        {
            var rows = await ExecuteReadAsync(OrdersRequest(TenantPrincipal("user-a", "tenant-a")));

            rows.Select(r => (string)r["name"]!).Should().NotContain("a-deleted");
            CapturedSql("orders").Should().MatchRegex(@"deleted_at\W*\s+IS\s+NULL");
        }

        // ---- (b) SQL is parameterized --------------------------------------

        [Fact]
        public async Task Read_CallerFilterValues_BindAsParametersNeverInline()
        {
            var rows = await ExecuteReadAsync(OrdersRequest(
                TenantPrincipal("user-a", "tenant-a"),
                new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?> { ["_eq"] = "a-second" },
                }));

            // The filter narrowed within the tenant scope, so it really executed…
            rows.Should().ContainSingle(r => (string)r["name"]! == "a-second");

            // …and neither the caller's value nor the tenant value appears in the
            // SQL text: both bind as @p parameters.
            var sql = CapturedSql("orders");
            sql.Should().NotContain("a-second", "user input must bind as a parameter, never concatenate");
            sql.Should().NotContain("tenant-a", "tenant values must bind as parameters, never concatenate");
            sql.Should().Contain("@", "the WHERE clause must reference bound parameters");
        }

        // ---- (c) column/table permissions ----------------------------------

        [Fact]
        public async Task Read_SelectingPolicyDeniedColumn_IsRejected()
        {
            await AssertReadRejectedAsync(new ConformanceReadRequest
            {
                Table = "documents",
                Columns = new[] { "id", "body" },
                Principal = TenantPrincipal("user-a", "tenant-a"),
                Endpoint = EndpointPath,
            }, "not permitted by authorization policy");
        }

        [Fact]
        public async Task Read_FilteringOnPolicyDeniedColumn_IsRejected()
        {
            // Filtering (not selecting) a denied column would otherwise leak the
            // value through a boolean oracle; the read guard must reject it too.
            await AssertReadRejectedAsync(new ConformanceReadRequest
            {
                Table = "documents",
                Columns = new[] { "id" },
                Filter = new Dictionary<string, object?>
                {
                    ["body"] = new Dictionary<string, object?> { ["_eq"] = "secret body" },
                },
                Principal = TenantPrincipal("user-a", "tenant-a"),
                Endpoint = EndpointPath,
            }, "not permitted by authorization policy");
        }

        [Fact]
        public async Task Read_WithoutTenantIdentity_FailsClosed()
        {
            await AssertReadRejectedAsync(OrdersRequest(principal: null), "Tenant context required");
        }

        /// <summary>
        /// Captures the generated SQL per table at the AfterExecute phase (the
        /// phase carrying SQL text), so the suite can assert on SQL no matter what
        /// wire the adapter speaks.
        /// </summary>
        private sealed class SqlCaptureObserver : IQueryObserver
        {
            private readonly object _gate = new();
            private readonly List<(string Table, string Sql)> _captured = new();

            public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };

            public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
            {
                lock (_gate)
                    _captured.Add((context.Table.DbName, context.Sql ?? string.Empty));
                return ValueTask.CompletedTask;
            }

            public IReadOnlyList<string> SqlFor(string table)
            {
                lock (_gate)
                    return _captured
                        .Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.Sql)
                        .ToArray();
            }
        }
    }
}

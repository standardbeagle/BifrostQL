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

    /// <summary>The mutation verb a <see cref="ConformanceMutationRequest"/> executes.</summary>
    public enum ConformanceMutationAction
    {
        Insert,
        Update,
        Delete,
    }

    /// <summary>
    /// A write request the conformance suite hands to an adapter under test — the
    /// mutation counterpart of <see cref="ConformanceReadRequest"/>. The derived
    /// suite translates it into the adapter's own wire format so the request
    /// travels the adapter's real request path.
    /// </summary>
    public sealed class ConformanceMutationRequest
    {
        /// <summary>Database table name (e.g. <c>orders</c>).</summary>
        public required string Table { get; init; }

        public required ConformanceMutationAction Action { get; init; }

        /// <summary>Column values (insert/update SET, delete predicate), by column name.</summary>
        public required IReadOnlyDictionary<string, object?> Data { get; init; }

        /// <summary>Optional positional primary-key values (composite-key safe).</summary>
        public IReadOnlyList<object?>? PrimaryKey { get; init; }

        /// <summary>
        /// The caller identity as the adapter's wire would deliver it, or null for
        /// an unauthenticated request.
        /// </summary>
        public ClaimsPrincipal? Principal { get; init; }

        /// <summary>The registered BifrostQL endpoint path the write targets.</summary>
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
    /// <item><b>Mutations run the transformer chain</b> (write-capable adapters
    /// only, see <see cref="AdapterSupportsMutations"/>) — inserts pin the caller's
    /// tenant, cross-tenant update/delete are no-ops, deletes on a soft-delete
    /// table soft-delete, and a missing tenant identity fails closed.</item>
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
    /// cannot prove fail-closed behavior. A write-capable adapter additionally
    /// overrides <see cref="AdapterSupportsMutations"/> (→ true) and
    /// <see cref="ExecuteMutationAsync"/>; a read-only adapter leaves both alone
    /// and the mutation facts are skipped. The base class owns the fixture: a shared
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

        /// <summary>
        /// Whether the adapter under test exposes writes. A read-only adapter is
        /// legitimate — leave this <c>false</c> (the default) and the mutation facts
        /// are skipped. An adapter that exposes ANY write surface MUST return
        /// <c>true</c> and override <see cref="ExecuteMutationAsync"/>; opting out
        /// while shipping writes would leave its mutation path unproven against
        /// tenant isolation and soft-delete semantics.
        /// </summary>
        protected virtual bool AdapterSupportsMutations => false;

        /// <summary>
        /// Whether the adapter's write surface exposes an INSERT (new-row) verb. Some
        /// write-capable adapters expose only UPDATE/DELETE against existing rows — a
        /// key-addressed protocol like RESP (SET = update, DEL = delete) has no wire
        /// command that creates a row. Such an adapter still sets
        /// <see cref="AdapterSupportsMutations"/> to <c>true</c> (its update/delete path
        /// MUST prove tenant scoping, soft-delete and fail-closed identity) but returns
        /// <c>false</c> here, so the two INSERT-specific facts are skipped honestly
        /// rather than faked through an update. An adapter with a genuine insert path
        /// (the default) leaves this <c>true</c> and proves those facts too.
        /// </summary>
        protected virtual bool AdapterSupportsInserts => true;

        /// <summary>
        /// Executes a write through the adapter's real request path and returns the
        /// adapter's scalar result (identity / key / affected count). Required when
        /// <see cref="AdapterSupportsMutations"/> is true. Server-side rejections
        /// must propagate as exceptions whose message chain contains the server
        /// error text.
        /// </summary>
        protected virtual Task<object?> ExecuteMutationAsync(ConformanceMutationRequest request)
            => throw new NotSupportedException(
                $"{GetType().Name} sets {nameof(AdapterSupportsMutations)} but does not override {nameof(ExecuteMutationAsync)}.");

        private static readonly string[] DefaultMetadataRules =
        {
            "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at }",
            "*.documents { policy-read-deny: body }",
        };

        /// <summary>
        /// The schema-metadata rules the fixture's tables carry. Defaults to the shared security
        /// fixture (orders tenant-filter + soft-delete, documents policy-read-deny). A derived suite
        /// whose adapter gates a surface by a per-table metadata opt-in (e.g. the gRPC front door's
        /// <c>grpc-write</c> write allow-list) overrides this to add that opt-in to the SAME tables —
        /// the tenant/soft-delete/policy semantics the kit asserts are unchanged, only an
        /// adapter-specific opt-in is added. Every other adapter inherits the default untouched.
        /// </summary>
        protected virtual IReadOnlyList<string> MetadataRules => DefaultMetadataRules;

        public virtual async Task InitializeAsync()
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

        public virtual async Task DisposeAsync()
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
                            e.Metadata = MetadataRules.ToArray();
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

        /// <summary>
        /// The text a fail-closed rejection surfaces on THIS adapter's wire. Adapters that
        /// forward the server error verbatim (the default) match the canonical server
        /// fragment. An adapter that sanitizes client-facing errors to a generic message —
        /// per protocol-adapter-security invariant 3, e.g. pgwire mapping every
        /// non-translation fault to one internal_error string — overrides this to its
        /// sanitized wire text: the fact still proves fail-closed (the read is rejected and
        /// no rows are delivered — <see cref="ExecuteReadAsync"/> must throw, never return
        /// rows), while honoring the adapter's contract that the specific reason is withheld
        /// from the wire. Overriding it does NOT let a swallowed error pass — the throw is
        /// still required; it only relaxes which text the surfaced rejection must carry.
        /// </summary>
        protected virtual string ExpectedRejectionFragment(string canonicalServerFragment) => canonicalServerFragment;

        /// <summary>
        /// The expected rejection text for the FILTER-on-a-policy-denied-column fact specifically.
        /// Defaults to <see cref="ExpectedRejectionFragment"/> (most adapters surface a denied column
        /// the same whether it is selected or filtered). A per-column-validating wire — e.g. gRPC's
        /// read compiler, which rejects a filter on a hidden column as an "unknown/unreadable field"
        /// (invariant 4: a hidden column is indistinguishable from a nonexistent one, a DIFFERENT
        /// sanitized signal than a table-level read denial) — overrides this to that field-validation
        /// text. The ASSERT is unchanged (the read is rejected, zero rows); only the EXPECTED text is
        /// adapter-relative, and only for the filter scenario.
        /// </summary>
        protected virtual string ExpectedFilterRejectionFragment(string canonicalServerFragment)
            => ExpectedRejectionFragment(canonicalServerFragment);

        /// <summary>
        /// The expected rejection text for a fail-closed WRITE specifically. Defaults to
        /// <see cref="ExpectedRejectionFragment"/>. A write-capable adapter whose write path sanitizes
        /// a fail-closed fault to a different generic status than its read path (e.g. gRPC maps a
        /// missing-tenant write to a generic INTERNAL while a missing-tenant read maps to
        /// PERMISSION_DENIED) overrides this. The ASSERT is unchanged (the write is rejected AND
        /// nothing is written); only the EXPECTED text is adapter-relative.
        /// </summary>
        protected virtual string ExpectedWriteRejectionFragment(string canonicalServerFragment)
            => ExpectedRejectionFragment(canonicalServerFragment);

        private async Task AssertReadRejectedAsync(ConformanceReadRequest request, string expectedErrorFragment)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteReadAsync(request));
            FlattenMessages(ex).Should().Contain(ExpectedRejectionFragment(expectedErrorFragment),
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
            var request = new ConformanceReadRequest
            {
                Table = "documents",
                Columns = new[] { "id" },
                Filter = new Dictionary<string, object?>
                {
                    ["body"] = new Dictionary<string, object?> { ["_eq"] = "secret body" },
                },
                Principal = TenantPrincipal("user-a", "tenant-a"),
                Endpoint = EndpointPath,
            };
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteReadAsync(request));
            FlattenMessages(ex).Should().Contain(
                ExpectedFilterRejectionFragment("not permitted by authorization policy"),
                "the adapter must surface the server-side rejection, not swallow or replace it");
        }

        [Fact]
        public async Task Read_WithoutTenantIdentity_FailsClosed()
        {
            await AssertReadRejectedAsync(OrdersRequest(principal: null), "Tenant context required");
        }

        // ---- (d) mutation facts (write-capable adapters only) ---------------
        //
        // Each fact returns early for read-only adapters (AdapterSupportsMutations
        // = false, the default) — read-only adapters are legitimate. A write-capable
        // adapter must pass all of them: they prove the adapter's writes travel the
        // mutation transformer chain (tenant pinning/scoping, soft-delete rewrite,
        // fail-closed identity), not a shortcut into raw SQL.

        /// <summary>Reads one scalar straight from the fixture database, bypassing the adapter.</summary>
        private async Task<object?> DbScalarAsync(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            var value = await cmd.ExecuteScalarAsync();
            return value == DBNull.Value ? null : value;
        }

        [Fact]
        public async Task Mutate_Insert_PinsTenantFromIdentity_IgnoringClientTenantValue()
        {
            if (!AdapterSupportsMutations || !AdapterSupportsInserts) return;

            // The caller tries to plant the row in tenant-b; the tenant mutation
            // transformer must pin it to the caller's own tenant.
            await ExecuteMutationAsync(new ConformanceMutationRequest
            {
                Table = "orders",
                Action = ConformanceMutationAction.Insert,
                Data = new Dictionary<string, object?>
                {
                    ["name"] = "conformance-insert",
                    ["tenant_id"] = "tenant-b",
                },
                Principal = TenantPrincipal("user-a", "tenant-a"),
                Endpoint = EndpointPath,
            });

            (await DbScalarAsync("SELECT tenant_id FROM orders WHERE name = 'conformance-insert'"))
                .Should().Be("tenant-a", "the tenant transformer pins the caller's tenant over the client value");
        }

        [Fact]
        public async Task Mutate_Insert_WithoutTenantIdentity_FailsClosed()
        {
            if (!AdapterSupportsMutations || !AdapterSupportsInserts) return;

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteMutationAsync(new ConformanceMutationRequest
            {
                Table = "orders",
                Action = ConformanceMutationAction.Insert,
                Data = new Dictionary<string, object?> { ["name"] = "no-identity" },
                Principal = null,
                Endpoint = EndpointPath,
            }));
            // The read fail-closed fact routes its expected wire text through ExpectedRejectionFragment
            // (a sanitizing adapter surfaces a generic message, not the internal reason); the write
            // fail-closed fact must do the SAME so a write-capable sanitizing adapter (e.g. gRPC) is
            // not forced to leak the internal cause. The ASSERT is unchanged — the write is rejected
            // AND nothing is written; only the EXPECTED text is adapter-relative (Lesson 1: adapt what
            // you EXPECT, never what you ASSERT).
            FlattenMessages(ex).Should().Contain(ExpectedWriteRejectionFragment("Tenant context required"),
                "the adapter must surface the server-side rejection, not swallow or replace it");

            (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE name = 'no-identity'"))
                .Should().Be(0L, "nothing may be written without a tenant identity");
        }

        [Fact]
        public async Task Mutate_CrossTenantUpdate_DoesNotTouchOtherTenantRows()
        {
            if (!AdapterSupportsMutations) return;

            // Tenant-b addresses tenant-a's row 1. The tenant transformer ANDs the
            // caller's tenant onto the WHERE, so the write matches nothing — the
            // same silent no-op the GraphQL path produces.
            await ExecuteMutationAsync(new ConformanceMutationRequest
            {
                Table = "orders",
                Action = ConformanceMutationAction.Update,
                Data = new Dictionary<string, object?> { ["name"] = "hijacked" },
                PrimaryKey = new object?[] { 1 },
                Principal = TenantPrincipal("user-b", "tenant-b"),
                Endpoint = EndpointPath,
            });

            (await DbScalarAsync("SELECT name FROM orders WHERE id = 1"))
                .Should().Be("a-first", "a caller must not update another tenant's rows");
        }

        [Fact]
        public async Task Mutate_CrossTenantDelete_DoesNotTouchOtherTenantRows()
        {
            if (!AdapterSupportsMutations) return;

            await ExecuteMutationAsync(new ConformanceMutationRequest
            {
                Table = "orders",
                Action = ConformanceMutationAction.Delete,
                Data = new Dictionary<string, object?>(),
                PrimaryKey = new object?[] { 2 },
                Principal = TenantPrincipal("user-b", "tenant-b"),
                Endpoint = EndpointPath,
            });

            (await DbScalarAsync("SELECT deleted_at FROM orders WHERE id = 2"))
                .Should().BeNull("a caller must not delete (even softly) another tenant's rows");
        }

        [Fact]
        public async Task Mutate_Delete_OnSoftDeleteTable_SoftDeletesInsteadOfRemoving()
        {
            if (!AdapterSupportsMutations) return;

            await ExecuteMutationAsync(new ConformanceMutationRequest
            {
                Table = "orders",
                Action = ConformanceMutationAction.Delete,
                Data = new Dictionary<string, object?>(),
                PrimaryKey = new object?[] { 2 },
                Principal = TenantPrincipal("user-a", "tenant-a"),
                Endpoint = EndpointPath,
            });

            // The soft-delete transformer rewrote DELETE into an UPDATE: the row
            // still exists, carries a deletion stamp, and vanishes from reads.
            (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE id = 2"))
                .Should().Be(1L, "soft delete must not physically remove the row");
            (await DbScalarAsync("SELECT deleted_at FROM orders WHERE id = 2"))
                .Should().NotBeNull("soft delete stamps the deleted_at column");

            var rows = await ExecuteReadAsync(OrdersRequest(TenantPrincipal("user-a", "tenant-a")));
            rows.Select(r => (string)r["name"]!).Should().NotContain("a-second",
                "soft-deleted rows never surface on reads");
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

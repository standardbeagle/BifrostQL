using System.Text;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
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
    /// stopped whether selected (rejected, or omitted where the wire hides denied
    /// columns by construction — see <see cref="DeniedColumnSelection"/>) or used
    /// as a filter oracle (always rejected), and a missing tenant identity fails
    /// closed.</item>
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
        /// Extra service registrations the surface under test needs beyond the endpoint itself
        /// (e.g. <c>AddBifrostEngine()</c> for the binary WebSocket transport, or a test
        /// authentication scheme). Default: none — an <see cref="IProtocolAdapter"/> hosted by its
        /// own connection handler needs nothing here.
        /// </summary>
        protected virtual void ConfigureAdapterServices(IServiceCollection services) { }

        /// <summary>
        /// The HTTP pipeline the surface under test needs mounted. Default: EMPTY — an
        /// <see cref="IProtocolAdapter"/> is hosted by a Kestrel connection handler /
        /// <c>IHostedService</c> and has no HTTP pipeline at all, which is why every existing
        /// derivation leaves this alone.
        ///
        /// <para>A front door that IS HTTP middleware (the binary WebSocket transport at
        /// <c>/bifrost-ws</c>) mounts itself here and is otherwise a full kit citizen: it executes
        /// arbitrary reads/writes against the fixture tables through its own real wire, so every
        /// fact applies unchanged. The hook exists so such a surface can derive the kit HONESTLY
        /// rather than being excused from it for a hosting-shape reason.</para>
        /// </summary>
        protected virtual void ConfigureAdapterPipeline(IApplicationBuilder app) { }

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
            // policy-actions: read makes the TABLE readable, so only the body column is denied
            // and the column-level facts below actually reach the column guard. Without it the
            // evaluator's empty AllowedActions denies documents WHOLESALE, and both column facts
            // pass on the table-level denial without the column guard ever running (vacuous —
            // .claude/rules/regression-test-non-vacuous.md, "A COLUMN-level policy fixture needs
            // policy-actions too").
            "*.documents { policy-actions: read; policy-read-deny: body }",
        };

        /// <summary>
        /// The schema-metadata rules the fixture's tables carry. Defaults to the shared security
        /// fixture (orders tenant-filter + soft-delete, documents readable with a policy-read-deny
        /// on the body column only). A derived suite
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
                    ConfigureAdapterServices(services);
                });
                web.Configure(ConfigureAdapterPipeline);
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
        /// The expected rejection text for the explicit-SELECTION of a policy-denied column
        /// specifically. Defaults to <see cref="ExpectedRejectionFragment"/>. A wire that hides
        /// denied columns from its caller-visible schema (OData's EDM, the MCP read compiler)
        /// answers an explicit selection of one exactly as it answers a NONEXISTENT column —
        /// its own unknown-field validation text, built from the caller's own arguments
        /// (invariant 4: hidden is indistinguishable from nonexistent) — and overrides this to
        /// that text. The ASSERT is unchanged (the read is rejected, zero rows).
        /// </summary>
        protected virtual string ExpectedSelectRejectionFragment(string canonicalServerFragment)
            => ExpectedRejectionFragment(canonicalServerFragment);

        /// <summary>How an adapter answers a read whose selection explicitly names a policy-read-denied column.</summary>
        protected enum DeniedColumnSelectionExpectation
        {
            /// <summary>The read is refused and zero rows are delivered (the query-path default:
            /// <c>PolicyFilterTransformer.AssertColumnsReadable</c> rejects rather than silently
            /// stripping a column the caller asked for).</summary>
            Reject,

            /// <summary>The read succeeds and the denied column is dropped from every row — the
            /// wire shape the caller's own visibility projection already advertises (LDAP omits
            /// the attribute — the only Omit derivation today; RESP's HGETALL reads every table
            /// column through the pipeline and so is a Reject), so an explicit denial would be
            /// the hidden-vs-nonexistent oracle of protocol-adapter-security invariant 9.</summary>
            Omit,
        }

        /// <summary>
        /// The per-adapter expectation for the explicit-selection fact. The default is
        /// <see cref="DeniedColumnSelectionExpectation.Reject"/> (the query pipeline's chosen
        /// mechanism — see <c>PolicyFilterTransformer</c>'s "Chosen mechanism" note); an adapter
        /// whose wire hides denied columns by construction overrides this to
        /// <see cref="DeniedColumnSelectionExpectation.Omit"/> WITH a one-line reason. The
        /// expectation is adapter-relative; the ASSERT is not: the denied value never reaches
        /// the caller either way.
        /// </summary>
        protected virtual DeniedColumnSelectionExpectation DeniedColumnSelection => DeniedColumnSelectionExpectation.Reject;

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
        public async Task Read_SelectingPolicyDeniedColumn_IsStopped()
        {
            // The fixture's policy-actions: read keeps the documents TABLE readable, so this fact
            // reaches the COLUMN guard — the expected fragment matches the column-deny message
            // ("...not permitted by authorization policy"), never the table-deny one
            // ("Access denied by authorization policy."), so a fixture regression back to a
            // wholesale table denial goes RED here instead of passing vacuously.
            var request = new ConformanceReadRequest
            {
                Table = "documents",
                Columns = new[] { "id", "body" },
                Principal = TenantPrincipal("user-a", "tenant-a"),
                Endpoint = EndpointPath,
            };

            if (DeniedColumnSelection == DeniedColumnSelectionExpectation.Omit)
            {
                var rows = await ExecuteReadAsync(request);
                rows.Should().NotBeEmpty(
                    "policy-actions: read makes documents readable; only the body column is denied");
                rows.Select(r => r.TryGetValue("body", out var value) ? value : null)
                    .Should().OnlyContain(
                        value => value == null,
                        "an omit adapter drops the denied column from every row — the denied value never crosses the wire");
                rows.Should().OnlyContain(
                    r => r.ContainsKey("id"),
                    "the readable columns still come through; omission must not swallow the row");
                return;
            }

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteReadAsync(request));
            FlattenMessages(ex).Should().Contain(
                ExpectedSelectRejectionFragment("not permitted by authorization policy"),
                "the adapter must surface the server-side rejection, not swallow or replace it");
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
        /// Cross-op-class WIRE PARITY for one condition: a caller with no tenant identity is
        /// refused with the SAME wire shape whether it reads or writes.
        ///
        /// <para>protocol-adapter-security invariant 10 is that a single error funnel is
        /// NECESSARY but not SUFFICIENT — the funnel maps by a CONDITION SIGNAL, so parity also
        /// needs every op class's upstream throw site to tag the same condition with the same
        /// signal. gRPC routed reads and writes through one GrpcStatusMapper and still answered
        /// PERMISSION_DENIED on read and a generic INTERNAL on write, because the read-side
        /// TenantFilterTransformer tagged its throw with AccessDeniedCode and the write-side
        /// TenantMutationTransformer threw a codeless error. That divergence is an oracle: the
        /// same denied caller learns something from the difference between the two answers.</para>
        ///
        /// <para>The divergence was invisible to every per-slice review and to every per-adapter
        /// suite, and the parity fact that caught it was written for gRPC ALONE — a wire-parity
        /// fact pinned on ONE of N sibling seams says nothing about the other N-1
        /// (regression-test-non-vacuous.md). This is that fact promoted to the shared kit, so
        /// every write-capable derivation carries it.</para>
        ///
        /// <para>The ASSERT is on the ACTUAL text both wires produced, not on the suite's own
        /// declarations: <see cref="ExpectedWriteRejectionFragment"/> exists so a sanitizing
        /// adapter need not leak its internal reason, NOT so a genuine cross-op divergence can be
        /// declared away — an adapter that overrides it to something other than its read fragment
        /// fails here, which is the review the override otherwise escapes.</para>
        /// </summary>
        [Fact]
        public async Task MissingTenantIdentity_ReadAndWrite_SurfaceTheSameWireRejection()
        {
            if (!AdapterSupportsMutations) return;

            const string condition = "Tenant context required";
            var readFragment = ExpectedRejectionFragment(condition);

            ExpectedWriteRejectionFragment(condition).Should().Be(readFragment,
                "one condition may not carry two declared wire shapes; the write-fragment hook "
                + "adapts a SANITIZING adapter's text, it does not license a cross-op divergence");

            var readError = await Assert.ThrowsAnyAsync<Exception>(
                () => ExecuteReadAsync(OrdersRequest(principal: null)));
            var writeError = await Assert.ThrowsAnyAsync<Exception>(
                () => ExecuteMutationAsync(new ConformanceMutationRequest
                {
                    // UPDATE, not INSERT: every write-capable adapter has an update verb, while
                    // a key-addressed wire (RESP, S3) has no row-creating one — gating this fact
                    // on AdapterSupportsInserts would silently exempt exactly those adapters.
                    Table = "orders",
                    Action = ConformanceMutationAction.Update,
                    Data = new Dictionary<string, object?> { ["name"] = "cross-op-parity" },
                    PrimaryKey = new object?[] { 1 },
                    Principal = null,
                    Endpoint = EndpointPath,
                }));

            FlattenMessages(readError).Should().Contain(readFragment);
            FlattenMessages(writeError).Should().Contain(readFragment,
                "the same condition must reach the wire as the same rejection on every op class; "
                + "a read/write difference is an oracle for a caller the deployment already denied");

            // Fail-closed on both halves, not merely "the two answers match".
            (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE name = 'cross-op-parity'"))
                .Should().Be(0L, "nothing may be written without a tenant identity");
        }

        // ---- (a2) malformed pre-auth wire input never escapes the handler ----
        //
        // Opt-in, same shape as the other adapter-surface flags. An adapter whose front door
        // decodes frames from an UNAUTHENTICATED peer sets AdapterSupportsMalformedFrameProbe
        // and drives its own connection handler with the bytes this fact supplies.

        /// <summary>What one malformed-input connection did, observed at the handler boundary.</summary>
        protected sealed class MalformedFrameOutcome
        {
            /// <summary>Whether the handler returned without letting an exception escape to the host.</summary>
            public required bool HandlerReturnedCleanly { get; init; }

            /// <summary>The escaped exception's type name, for the failure message; null when none escaped.</summary>
            public string? EscapedExceptionType { get; init; }

            /// <summary>Whether the handler finished the connection (answered and/or closed) rather than hanging.</summary>
            public required bool ConnectionCompleted { get; init; }
        }

        /// <summary>
        /// Whether this adapter owns a connection handler that decodes an unauthenticated peer's
        /// bytes and can be driven directly with a stream. Default false: HTTP-mounted front doors
        /// ride Kestrel's own framing and have no such surface.
        /// </summary>
        protected virtual bool AdapterSupportsMalformedFrameProbe => false;

        /// <summary>
        /// Runs ONE connection whose entire wire is <paramref name="frame"/> — arbitrary bytes from
        /// an unauthenticated peer, delivered before any credential — through the adapter's real
        /// connection handler, and reports what happened at the handler boundary.
        /// </summary>
        protected virtual Task<MalformedFrameOutcome> ProbeMalformedFrameAsync(byte[] frame)
            => throw new NotSupportedException(
                "Set AdapterSupportsMalformedFrameProbe = true and implement ProbeMalformedFrameAsync.");

        /// <summary>
        /// One connection's wire, scripted: reads deliver <c>frame</c> and then EOF, writes are
        /// captured. Deterministic by construction — no socket, no timing — so the corpus below
        /// replays identically on every host.
        /// </summary>
        protected sealed class ScriptedWireStream : Stream
        {
            private readonly byte[] _inbound;
            private int _position;
            private readonly MemoryStream _outbound = new();

            public ScriptedWireStream(byte[] inbound) => _inbound = inbound;

            /// <summary>Bytes the handler wrote back before closing.</summary>
            public byte[] Written => _outbound.ToArray();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inbound.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var available = Math.Min(count, _inbound.Length - _position);
                if (available <= 0) return 0;
                Array.Copy(_inbound, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            public override void Write(byte[] buffer, int offset, int count)
                => _outbound.Write(buffer, offset, count);
        }

        /// <summary>
        /// Drives one scripted connection through <paramref name="run"/> — the adapter's real
        /// connection-handler entry point — and reports what happened at that boundary. The
        /// timeout is the "never hang" half of the fact: a handler that neither answers nor
        /// closes holds an admission slot for free.
        /// </summary>
        protected static async Task<MalformedFrameOutcome> ProbeAsync(
            byte[] frame, Func<Stream, CancellationToken, Task> run)
        {
            using var wire = new ScriptedWireStream(frame);
            using var cts = new CancellationTokenSource();
            string? escaped = null;
            var completed = false;
            try
            {
                await run(wire, cts.Token).WaitAsync(TimeSpan.FromSeconds(30));
                completed = true;
            }
            catch (TimeoutException)
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                escaped = ex.GetType().FullName;
            }

            return new MalformedFrameOutcome
            {
                HandlerReturnedCleanly = escaped is null,
                EscapedExceptionType = escaped,
                ConnectionCompleted = completed,
            };
        }

        /// <summary>
        /// Deterministic malformed-input corpus. Pinned seeds and fixed shapes, never a live RNG:
        /// a fuzz fact that cannot be replayed byte-for-byte reports a failure nobody can reproduce.
        /// </summary>
        private static IEnumerable<(string Name, byte[] Bytes)> MalformedFrames()
        {
            yield return ("empty", Array.Empty<byte>());
            yield return ("nul", new byte[] { 0 });
            yield return ("truncated-length-prefix", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            yield return ("high-bytes", Enumerable.Repeat((byte)0x80, 64).ToArray());
            yield return ("printable-garbage", Encoding.ASCII.GetBytes(new string('A', 512)));
            // Deeply nested-looking prefixes: the shape that turns a recursive decoder without a
            // depth cap into an uncatchable StackOverflowException (invariant 6).
            yield return ("repeated-aggregate-header",
                Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*1\r\n", 4096))));
            yield return ("repeated-ber-sequence-header",
                Enumerable.Range(0, 4096).SelectMany(_ => new byte[] { 0x30, 0x84, 0x7F, 0xFF }).ToArray());

            foreach (var seed in new[] { 1, 7, 13, 42, 1337 })
            {
                var rng = new Random(seed);
                var bytes = new byte[256];
                rng.NextBytes(bytes);
                yield return ($"seed-{seed}", bytes);
            }
        }

        /// <summary>
        /// Malformed bytes from an unauthenticated peer close the connection; they never escape
        /// the handler and never hang the caller.
        ///
        /// <para>protocol-adapter-security invariant 1: an exception that does not match the
        /// connection handler's catch filter reaches the host (Kestrel) unhandled on
        /// adversary-controlled input. Invariant 5 is the same failure one level down — a decode
        /// built on a <c>.Parse</c>-family call that catches only <c>FormatException</c> lets an
        /// <c>OverflowException</c> out on a boundary value, tearing the connection down with no
        /// wire reply. Both shipped, on pgwire, twice.</para>
        ///
        /// <para>The corpus is fixed and its seeds are pinned, so a failure is replayable
        /// byte-for-byte; the fact names the frame that escaped rather than reporting an
        /// anonymous fuzz failure.</para>
        /// </summary>
        [Fact]
        public async Task MalformedPreAuthFrames_NeverEscapeTheHandler()
        {
            if (!AdapterSupportsMalformedFrameProbe) return;

            var escaped = new List<string>();
            var hung = new List<string>();
            foreach (var (name, bytes) in MalformedFrames())
            {
                var outcome = await ProbeMalformedFrameAsync(bytes);
                if (!outcome.HandlerReturnedCleanly)
                    escaped.Add($"{name} -> {outcome.EscapedExceptionType ?? "unknown"}");
                if (!outcome.ConnectionCompleted)
                    hung.Add(name);
            }

            escaped.Should().BeEmpty(
                "an unauthenticated peer's bytes must never produce an exception the connection "
                + "handler's catch filter misses (invariant 1); escaping frames: "
                + string.Join(", ", escaped));
            hung.Should().BeEmpty(
                "every malformed frame is answered and/or closed — a peer that neither gets a "
                + "reply nor a close holds an admission slot for free; hanging frames: "
                + string.Join(", ", hung));
        }

        // ---- (e) pre-auth attempt limiter (adapters with a credential handshake) --
        //
        // Opt-in, same shape as AdapterSupportsMutations: an adapter whose wire carries a
        // credential-bearing handshake (LDAP bind, RESP AUTH, pgwire password) sets
        // AdapterSupportsAuthRateLimit = true and implements AttemptAuthAsync; adapters
        // without the surface (HTTP front doors riding Kestrel, the echo fixture) inherit
        // the default and the fact stays silent — no skip noise, no forced stub.

        /// <summary>The outcome of one credential attempt, observed through the adapter's real wire.</summary>
        protected sealed class AuthAttemptOutcome
        {
            /// <summary>Whether the wire refused the attempt (wrong secret AND rate-limited both count).</summary>
            public required bool Refused { get; init; }

            /// <summary>The exact refusal text/code as it appeared on the wire; null when the attempt succeeded.</summary>
            public string? WireText { get; init; }

            /// <summary>
            /// Whether the attempt caused the credential store to be consulted. The derivation
            /// observes this with a counting credential store: the whole point of the limiter is
            /// that an over-cap refusal costs NO resolution work.
            /// </summary>
            public required bool CredentialResolved { get; init; }
        }

        /// <summary>
        /// Whether the adapter's credential handshake is bounded by a per-source/per-account
        /// pre-auth attempt limiter (AGENTS.md listener posture: "credential-bearing handshake
        /// must be rate-limited"). Default false; an adapter without the surface leaves it alone.
        /// </summary>
        protected virtual bool AdapterSupportsAuthRateLimit => false;

        /// <summary>
        /// The attempt budget the derivation configures BOTH limiter axes to. Required when
        /// <see cref="AdapterSupportsAuthRateLimit"/> is true.
        /// </summary>
        protected virtual int AuthAttemptBudget => throw new NotSupportedException(
            $"{GetType().Name} sets {nameof(AdapterSupportsAuthRateLimit)} but does not override {nameof(AuthAttemptBudget)}.");

        /// <summary>An account that EXISTS in the derivation's credential store.</summary>
        protected virtual string KnownAuthAccount => "conformance-known-account";

        /// <summary>An account that does NOT exist in the derivation's credential store.</summary>
        protected virtual string UnknownAuthAccount => "conformance-unknown-account";

        /// <summary>
        /// Performs one credential attempt through the adapter's real handshake with a WRONG
        /// secret and reports what the wire answered and whether the credential store was
        /// consulted. Required when <see cref="AdapterSupportsAuthRateLimit"/> is true. Attempts
        /// against one fact share the derivation's limiter state (same source, same process), so
        /// the kit can burn the budget and then observe the over-cap refusal.
        /// </summary>
        protected virtual Task<AuthAttemptOutcome> AttemptAuthAsync(string account, string secret)
            => throw new NotSupportedException(
                $"{GetType().Name} sets {nameof(AdapterSupportsAuthRateLimit)} but does not override {nameof(AttemptAuthAsync)}.");

        [Fact]
        public async Task Auth_OverCapAttempt_IsRefusedBeforeCredentialResolution_WithAccountBlindRefusal()
        {
            if (!AdapterSupportsAuthRateLimit) return;

            const string wrongSecret = "conformance-wrong-secret";

            // Burn the budget against the KNOWN account. Every IN-budget attempt must still reach
            // the credential store — only the over-cap refusal may skip resolution, or the limiter
            // is gating the wrong side of the handshake.
            for (var attempt = 1; attempt <= AuthAttemptBudget; attempt++)
            {
                var within = await AttemptAuthAsync(KnownAuthAccount, wrongSecret);
                within.Refused.Should().BeTrue("a wrong secret is refused even inside the budget");
                within.CredentialResolved.Should().BeTrue(
                    "attempt {0} is inside the budget; the credential must still be resolved", attempt);
            }

            // Past the cap the SAME attempt is refused BEFORE the credential is resolved — a
            // sustained guessing loop must cost the front door nothing.
            var overCapKnown = await AttemptAuthAsync(KnownAuthAccount, wrongSecret);
            overCapKnown.Refused.Should().BeTrue("the over-cap attempt is refused");
            overCapKnown.CredentialResolved.Should().BeFalse(
                "past the cap the refusal precedes credential resolution — a limiter that resolves first bounds nothing");

            var overCapUnknown = await AttemptAuthAsync(UnknownAuthAccount, wrongSecret);
            overCapUnknown.Refused.Should().BeTrue("an unknown account over the cap is refused the same way");
            overCapUnknown.CredentialResolved.Should().BeFalse(
                "the unknown account is over the same per-source cap and must not be resolved either");
            overCapUnknown.WireText.Should().Be(overCapKnown.WireText,
                "a rate-limit refusal that varies with account existence is an enumeration oracle — byte equality, not Contains");
        }

        // ---- (f) total-frame byte cap (adapters with a framed wire) ----------
        //
        // Opt-in, same shape as AdapterSupportsMutations: an adapter whose decoder frames
        // untrusted bytes (RESP aggregates, LDAP BER envelopes, pgwire messages) sets
        // AdapterSupportsFrameLimit = true and implements ProbeFrameLimitAsync; HTTP front doors
        // riding Kestrel and the echo fixture inherit the default and stay silent.

        /// <summary>What the derivation observed when probing its decoder's total-frame byte budget.</summary>
        protected sealed class FrameLimitProbe
        {
            /// <summary>Whether the oversized frame was refused at all.</summary>
            public required bool OversizedRefused { get; init; }

            /// <summary>Bytes the decoder actually pulled from the stream for the oversized frame (counting stream).</summary>
            public required long OversizedBytesRead { get; init; }

            /// <summary>The payload byte count the oversized frame DECLARED.</summary>
            public required long OversizedDeclaredBytes { get; init; }

            /// <summary>Whether two consecutive in-budget top-level frames both decoded on one stream.</summary>
            public required bool TopLevelBudgetResetVerified { get; init; }
        }

        /// <summary>
        /// Whether the adapter's wire decoder bounds the TOTAL byte length of one top-level frame
        /// (per-element caps are not sufficient — their product is the real bound). Default false.
        /// </summary>
        protected virtual bool AdapterSupportsFrameLimit => false;

        /// <summary>
        /// Drives the adapter's real decoder with (1) one frame whose declared size exceeds the
        /// frame budget, through a counting stream, and (2) two consecutive in-budget top-level
        /// frames on one stream. Required when <see cref="AdapterSupportsFrameLimit"/> is true.
        /// </summary>
        protected virtual Task<FrameLimitProbe> ProbeFrameLimitAsync()
            => throw new NotSupportedException(
                $"{GetType().Name} sets {nameof(AdapterSupportsFrameLimit)} but does not override {nameof(ProbeFrameLimitAsync)}.");

        [Fact]
        public async Task Frame_OverBudget_IsRefusedBeforePayloadIsRead_AndBudgetResetsPerTopLevelFrame()
        {
            if (!AdapterSupportsFrameLimit) return;

            var probe = await ProbeFrameLimitAsync();

            probe.OversizedRefused.Should().BeTrue(
                "a frame whose declared size exceeds the budget must be refused");
            probe.OversizedBytesRead.Should().BeLessThan(probe.OversizedDeclaredBytes,
                "the refusal must precede pulling the declared payload — a cap that fires only after the bytes are materialized bounds nothing");
            probe.TopLevelBudgetResetVerified.Should().BeTrue(
                "the byte budget resets only at a top-level frame boundary; consecutive in-budget frames must both decode");
        }

        // ---- (b) admission slot is held BEFORE the TLS handshake -------------
        //
        // Opt-in, same shape as AdapterSupportsMutations: an adapter that terminates TLS on its
        // own listener (implicit TLS, or an in-band upgrade it negotiates itself) sets
        // AdapterSupportsTlsAdmissionProbe = true and implements ProbeTlsAdmissionAsync. HTTP
        // front doors riding the host's Kestrel do not own the accept and inherit the default.

        /// <summary>What the derivation observed when probing admission against a TLS front door.</summary>
        protected sealed class TlsAdmissionProbe
        {
            /// <summary>
            /// Slots the adapter counted as held after a peer connected and sent NOTHING — no
            /// ClientHello, no negotiation packet, not one byte.
            /// </summary>
            public required int SlotsHeldBySilentPeer { get; init; }

            /// <summary>Whether a SECOND peer, arriving with the only slot held, was closed.</summary>
            public required bool OverCapPeerClosed { get; init; }

            /// <summary>
            /// Whether that second peer nonetheless completed a TLS handshake with the listener.
            /// </summary>
            public required bool OverCapPeerCompletedTlsHandshake { get; init; }
        }

        /// <summary>
        /// Whether this adapter terminates TLS on its own listener, so the ORDER of admission and
        /// handshake is its own to get right. Default false.
        /// </summary>
        protected virtual bool AdapterSupportsTlsAdmissionProbe => false;

        /// <summary>
        /// Drives a REAL listener of this adapter, configured with a cap of one connection, and
        /// reports (1) whether a silent peer that never starts the handshake holds a slot and
        /// (2) whether the next peer is turned away without one. Required when
        /// <see cref="AdapterSupportsTlsAdmissionProbe"/> is true. The ordering under test is the
        /// listener's own middleware registration order, which no in-process stream harness can
        /// observe — the probe must bind a port.
        /// </summary>
        protected virtual Task<TlsAdmissionProbe> ProbeTlsAdmissionAsync()
            => throw new NotSupportedException(
                $"{GetType().Name} sets {nameof(AdapterSupportsTlsAdmissionProbe)} but does not override {nameof(ProbeTlsAdmissionAsync)}.");

        [Fact]
        public async Task AdmissionSlot_IsHeldBeforeTheTlsHandshake()
        {
            if (!AdapterSupportsTlsAdmissionProbe) return;

            var probe = await ProbeTlsAdmissionAsync();

            probe.SlotsHeldBySilentPeer.Should().Be(1,
                "the slot is reserved at ACCEPT. A cap applied after the handshake counts only the "
                + "sessions that got that far, while an unauthenticated peer forces an unbounded "
                + "number of concurrent handshakes — an asymmetric private-key operation each — "
                + "outside the cap. A silent socket is the cheapest attack on a TLS front door, and "
                + "it must cost a slot");
            probe.OverCapPeerClosed.Should().BeTrue(
                "with the only slot held, the next peer must be turned away at the door");
            probe.OverCapPeerCompletedTlsHandshake.Should().BeFalse(
                "the refusal must precede the handshake: a peer that completes one has already "
                + "spent the resource the cap exists to bound, whatever the listener answers after");
        }

        // ---- (g) continuation-token integrity (adapters with a paged read) -----
        //
        // Opt-in, same shape as AdapterSupportsMutations: an adapter whose wire paginates a
        // read with an opaque continuation token (LDAP paged-results cookie, OData $skiptoken,
        // gRPC page token, RESP SCAN cursor) sets AdapterSupportsContinuationTokens = true and
        // implements ReplayContinuationAsync; adapters without the surface (the echo fixture)
        // inherit the default and the fact stays silent — no skip noise, no forced stub.

        /// <summary>The tamper the conformance kit applies to a continuation token before replaying it.</summary>
        protected enum ContinuationTamper
        {
            /// <summary>Minted with a MAC key the server does not hold.</summary>
            Forged,

            /// <summary>A genuinely issued token whose bytes were altered after issue.</summary>
            Tampered,

            /// <summary>A valid token replayed against a DIFFERENT table/search shape whose key
            /// arity is the SAME as the issuing context's, so an arity check on the payload cannot
            /// stand in for the integrity guard.</summary>
            CrossContext,

            /// <summary>A valid token replayed under a different caller identity.</summary>
            CrossIdentity,

            /// <summary>A valid token replayed after its TTL elapsed.</summary>
            Expired,

            /// <summary>Wire bytes that were never a token at all.</summary>
            Unparseable,
        }

        /// <summary>What the derivation observed replaying one tampered continuation token.</summary>
        protected sealed class ContinuationReplayOutcome
        {
            /// <summary>Whether the replay was refused at all.</summary>
            public required bool Refused { get; init; }

            /// <summary>
            /// The wire-visible refusal identity (error line, result code, or exception message) the
            /// adapter produced; null when the replay was not refused. The kit asserts byte equality
            /// across every tamper — any variation is an oracle separating "forged" from "expired".
            /// </summary>
            public required string? WireText { get; init; }

            /// <summary>
            /// Whether the replay was accepted as "start the scan from the beginning". A token that
            /// does not validate must be refused EXPLICITLY; treating it as the start sentinel turns
            /// a tampered token into a silent full re-scan.
            /// </summary>
            public required bool ResumedFromStart { get; init; }
        }

        /// <summary>
        /// Whether the adapter's wire carries an opaque, integrity-protected continuation token for
        /// paged reads (AGENTS.md listener posture: "continuation/paging cookie must be MAC'd").
        /// Default false; an adapter without the surface leaves it alone.
        /// </summary>
        protected virtual bool AdapterSupportsContinuationTokens => false;

        /// <summary>
        /// Mints a continuation token through the adapter's real cursor type, applies
        /// <paramref name="tamper"/>, replays it against the binding the LIVE continuation request
        /// would re-derive, and reports the outcome. Required when
        /// <see cref="AdapterSupportsContinuationTokens"/> is true. The cross-context replay MUST
        /// target a table/search shape with the same key arity as the issuing context.
        /// </summary>
        protected virtual Task<ContinuationReplayOutcome> ReplayContinuationAsync(ContinuationTamper tamper)
            => throw new NotSupportedException(
                $"{GetType().Name} sets {nameof(AdapterSupportsContinuationTokens)} but does not override {nameof(ReplayContinuationAsync)}.");

        [Fact]
        public async Task ContinuationToken_ForgeTamperReplayExpiryUnparseable_AllRefuseIdentically()
        {
            if (!AdapterSupportsContinuationTokens) return;

            var outcomes = new Dictionary<ContinuationTamper, ContinuationReplayOutcome>();
            foreach (var tamper in Enum.GetValues<ContinuationTamper>())
                outcomes[tamper] = await ReplayContinuationAsync(tamper);

            foreach (var (tamper, outcome) in outcomes)
            {
                outcome.Refused.Should().BeTrue(
                    "a {0} continuation token must be refused explicitly — accepting it is a replay or re-scan hole",
                    tamper);
                outcome.ResumedFromStart.Should().BeFalse(
                    "a {0} continuation token must never restart the scan from the beginning",
                    tamper);
                outcome.WireText.Should().NotBeNull(
                    "a refused {0} continuation must carry the adapter's wire refusal text", tamper);
            }

            outcomes.Values.Select(o => o.WireText).Distinct().Should().ContainSingle(
                "forgery, tamper, cross-context replay, cross-identity replay, expiry and garbage are ONE refusal outcome — any variation is an oracle");
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

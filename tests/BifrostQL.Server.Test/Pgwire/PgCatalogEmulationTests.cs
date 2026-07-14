using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Catalog-emulation tests for pgwire slice 4, driven end to end over a loopback
    /// socket through a real authenticated handshake. The catalog is synthesized from a
    /// mocked <see cref="IDbModel"/> and filtered by the authenticated identity using the
    /// same authoritative policy check the query path enforces. Two identities with
    /// different table/column visibility prove the catalog never leaks a relation the
    /// caller cannot query.
    /// </summary>
    public sealed class PgCatalogEmulationTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        // ---- identity filtering: information_schema.tables -------------------

        [Fact]
        public async Task InfoSchemaTables_ListsOnlyTablesTheIdentityMayRead()
        {
            var executor = CatalogExecutor();

            // Non-admin (member) sees the unrestricted tables and read-granted tables,
            // but NOT a table whose policy denies read, and NOT a table with unparseable
            // policy metadata (fail closed).
            await using var member = await PgSession.StartAsync(executor, MemberPrincipal());
            await member.Client.SendQueryAsync("SELECT table_name FROM information_schema.tables");
            var memberTables = (await member.Client.ReadQueryResultAsync().WaitAsync(Timeout));

            memberTables.HasError.Should().BeFalse();
            var memberNames = memberTables.Rows.Select(r => r[0]).ToList();
            memberNames.Should().Contain(new[] { "orders", "profiles" });
            memberNames.Should().NotContain("audit_log");   // policy denies read to non-admins
            memberNames.Should().NotContain("broken");      // malformed policy → fail closed

            // Admin additionally sees the read-denied table, still not the broken one.
            await using var admin = await PgSession.StartAsync(executor, AdminPrincipal());
            await admin.Client.SendQueryAsync("SELECT table_name FROM information_schema.tables");
            var adminTables = await admin.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            var adminNames = adminTables.Rows.Select(r => r[0]).ToList();
            adminNames.Should().Contain(new[] { "orders", "profiles", "audit_log" });
            adminNames.Should().NotContain("broken");       // fail closed for everyone
        }

        // ---- psql \d / \dt relation list -------------------------------------

        // The exact query psql (v16) issues for \dt: a pg_class ⋈ pg_namespace join
        // with a CASE-mapped relkind, pg_get_userbyid(relowner), the relkind IN filter,
        // system-namespace exclusions, pg_table_is_visible(oid), and positional
        // ORDER BY 1,2 — none of which the slice-3 subset grammar accepts.
        private const string PsqlDtQuery =
            "SELECT n.nspname as \"Schema\",\n" +
            "  c.relname as \"Name\",\n" +
            "  CASE c.relkind WHEN 'r' THEN 'table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' WHEN 'S' THEN 'sequence' WHEN 'f' THEN 'foreign table' WHEN 'p' THEN 'partitioned table' END as \"Type\",\n" +
            "  pg_catalog.pg_get_userbyid(c.relowner) as \"Owner\"\n" +
            "FROM pg_catalog.pg_class c\n" +
            "     LEFT JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n" +
            "WHERE c.relkind IN ('r','p','')\n" +
            "      AND n.nspname <> 'pg_catalog'\n" +
            "      AND n.nspname !~ '^pg_toast'\n" +
            "      AND n.nspname <> 'information_schema'\n" +
            "  AND pg_catalog.pg_table_is_visible(c.oid)\n" +
            "ORDER BY 1,2;";

        [Fact]
        public async Task PsqlDt_ListsPermittedTables_OrderedAndIdentityFiltered()
        {
            var executor = CatalogExecutor();

            // Member: sees only the tables it may read, ordered by (schema, name);
            // read-denied and fail-closed tables are absent.
            await using var member = await PgSession.StartAsync(executor, MemberPrincipal());
            await member.Client.SendQueryAsync(PsqlDtQuery);
            var memberResult = await member.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            memberResult.HasError.Should().BeFalse();
            memberResult.Fields.Select(f => f.Name).Should().Equal("Schema", "Name", "Type", "Owner");
            memberResult.Rows.Select(r => r[1]).Should().Equal("orders", "profiles"); // ORDER BY 1,2
            memberResult.Rows.Select(r => r[1]).Should().NotContain("audit_log");      // read-denied
            memberResult.Rows.Select(r => r[1]).Should().NotContain("broken");         // fail closed
            memberResult.Rows.Should().OnlyContain(r => (string?)r[0] == "dbo");        // Schema
            memberResult.Rows.Should().OnlyContain(r => (string?)r[2] == "table");      // Type (relkind 'r')

            // Admin: additionally sees the read-denied table, still ordered, broken absent.
            await using var admin = await PgSession.StartAsync(executor, AdminPrincipal());
            await admin.Client.SendQueryAsync(PsqlDtQuery);
            var adminResult = await admin.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            adminResult.HasError.Should().BeFalse();
            adminResult.Rows.Select(r => r[1]).Should().Equal("audit_log", "orders", "profiles");
        }

        // ---- identity filtering: pg_class ------------------------------------

        [Fact]
        public async Task PgClass_DescribesVisibleTables_WithBaseTableKind()
        {
            var executor = CatalogExecutor();
            await using var member = await PgSession.StartAsync(executor, MemberPrincipal());

            await member.Client.SendQueryAsync("SELECT relname, relkind, relnatts FROM pg_catalog.pg_class");
            var result = await member.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Equal("relname", "relkind", "relnatts");

            var byName = result.Rows.ToDictionary(r => r[0]!, r => r);
            byName.Keys.Should().Contain(new[] { "orders", "profiles" });
            byName.Keys.Should().NotContain("audit_log"); // read-denied table absent from pg_class
            byName["orders"][1].Should().Be("r");          // relkind = ordinary table
        }

        // ---- pg_attribute: attnames / type oids / notnull --------------------

        [Fact]
        public async Task PgAttribute_ReportsColumnNamesTypeOidsAndNotNull_RespectingColumnDeny()
        {
            var executor = CatalogExecutor();
            await using var member = await PgSession.StartAsync(executor, MemberPrincipal());

            await member.Client.SendQueryAsync(
                "SELECT attname, atttypid, attnum, attnotnull FROM pg_catalog.pg_attribute");
            var result = await member.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            var attnames = result.Rows.Select(r => r[0]).ToList();

            // The primary-key 'id' column is described with the int4 OID, attnum 1, NOT NULL.
            var idRow = result.Rows.First(r => r[0] == "id");
            idRow[1].Should().Be(PgTypeMap.OidInt4.ToString()); // atttypid
            idRow[2].Should().Be("1");                          // attnum
            idRow[3].Should().Be("t");                          // attnotnull (id is not nullable)

            // A read-denied column is absent for the member.
            attnames.Should().NotContain("ssn");
        }

        // ---- information_schema.columns: names + data types + column deny -----

        [Fact]
        public async Task InfoSchemaColumns_ReportsColumnNamesAndDataTypes_FilteredByColumnPolicy()
        {
            var executor = CatalogExecutor();

            // Member: profiles is readable, but the ssn column is read-denied → hidden.
            await using var member = await PgSession.StartAsync(executor, MemberPrincipal());
            await member.Client.SendQueryAsync(
                "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'profiles'");
            var memberCols = await member.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            memberCols.HasError.Should().BeFalse();
            var memberColNames = memberCols.Rows.Select(r => r[0]).ToList();
            memberColNames.Should().Contain(new[] { "id", "name" });
            memberColNames.Should().NotContain("ssn");
            memberCols.Rows.First(r => r[0] == "id")[1].Should().Be("integer");
            memberCols.Rows.First(r => r[0] == "name")[1].Should().Be("character varying");

            // Admin sees the read-denied column too.
            await using var admin = await PgSession.StartAsync(executor, AdminPrincipal());
            await admin.Client.SendQueryAsync(
                "SELECT column_name FROM information_schema.columns WHERE table_name = 'profiles'");
            var adminCols = await admin.Client.ReadQueryResultAsync().WaitAsync(Timeout);
            adminCols.Rows.Select(r => r[0]).Should().Contain("ssn");
        }

        // ---- scalar introspection --------------------------------------------

        [Fact]
        public async Task VersionFunction_ReturnsSingleVersionRow()
        {
            var executor = CatalogExecutor();
            await using var session = await PgSession.StartAsync(executor, MemberPrincipal());

            await session.Client.SendQueryAsync("SELECT version()");
            var result = await session.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Equal("version");
            result.Rows.Should().ContainSingle().Which[0].Should().Contain("BifrostQL");
        }

        // ---- auth'd path integrity: passthrough + clean errors ---------------

        [Fact]
        public async Task NonCatalogQuery_StillRoutesToRealReadPath()
        {
            // A plain SELECT against a model table is not a catalog query: it must fall
            // through to the executor (proving the responder returns null for it).
            var executor = CatalogExecutor(ordersRows: new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = 42, ["customer"] = "acme" },
            });
            await using var session = await PgSession.StartAsync(executor, MemberPrincipal());

            await session.Client.SendQueryAsync("SELECT id FROM orders");
            var result = await session.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Rows.Should().ContainSingle().Which.Should().Equal("42");
        }

        [Fact]
        public async Task UnknownCatalogColumn_MapsToCleanSyntaxError_AndSessionSurvives()
        {
            var executor = CatalogExecutor();
            await using var session = await PgSession.StartAsync(executor, MemberPrincipal());

            // A catalog query that references a column the emulated relation does not
            // expose is a clean client error, not an internal fault or a wrong answer.
            await session.Client.SendQueryAsync("SELECT nope FROM information_schema.tables");
            var errored = await session.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            errored.HasError.Should().BeTrue();
            errored.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
            errored.TransactionStatus.Should().Be('I');

            // The same connection still serves a valid catalog query afterward.
            await session.Client.SendQueryAsync("SELECT table_name FROM information_schema.tables");
            var ok = await session.Client.ReadQueryResultAsync().WaitAsync(Timeout);
            ok.HasError.Should().BeFalse();
        }

        // ---- fixtures --------------------------------------------------------

        private sealed class CapturedIntent { public QueryIntent? Intent { get; set; } }

        private static IQueryIntentExecutor CatalogExecutor(
            IReadOnlyList<IReadOnlyDictionary<string, object?>>? ordersRows = null)
        {
            // Build every table substitute BEFORE configuring model.Tables so NSubstitute
            // does not see nested substitute configuration inside a Returns(...) call.
            var orders = Table("orders",
                Col("id", "int", ordinal: 1, isPrimaryKey: true),
                Col("customer", "varchar", ordinal: 2, isNullable: true));

            var profiles = Table("profiles",
                policyActions: "read", readDeny: "ssn",
                columns: new[]
                {
                    Col("id", "int", ordinal: 1, isPrimaryKey: true),
                    Col("name", "varchar", ordinal: 2, isNullable: true),
                    Col("ssn", "varchar", ordinal: 3, isNullable: true),
                });

            // Read denied to non-admins (policy grants only create).
            var auditLog = Table("audit_log",
                policyActions: "create",
                columns: new[] { Col("id", "int", ordinal: 1, isPrimaryKey: true) });

            // Malformed policy metadata: must be excluded for EVERYONE (fail closed).
            var broken = Table("broken",
                policyActions: "frobnicate",
                columns: new[] { Col("id", "int", ordinal: 1, isPrimaryKey: true) });

            var tables = new[] { orders, profiles, auditLog, broken };

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(tables);

            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            executor.ExecuteAsync(Arg.Any<QueryIntent>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new QueryIntentResult
                {
                    Rows = ordersRows ?? Array.Empty<IReadOnlyDictionary<string, object?>>(),
                    Sql = "-- mocked",
                }));
            return executor;
        }

        private static IDbTable Table(
            string name,
            params ColumnDto[] columns) =>
            Table(name, null, null, columns);

        private static IDbTable Table(
            string name,
            string? policyActions = null,
            string? readDeny = null,
            ColumnDto[]? columns = null)
        {
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns(name);
            table.GraphQlName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns ?? Array.Empty<ColumnDto>());
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns(policyActions);
            table.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns(readDeny);
            return table;
        }

        private static ColumnDto Col(
            string name, string dataType, int ordinal, bool isNullable = false, bool isPrimaryKey = false) =>
            new()
            {
                ColumnName = name,
                GraphQlName = name,
                DataType = dataType,
                OrdinalPosition = ordinal,
                IsNullable = isNullable,
                IsPrimaryKey = isPrimaryKey,
            };

        private static ClaimsPrincipal AdminPrincipal() => RolePrincipal("user-admin", "admin");
        private static ClaimsPrincipal MemberPrincipal() => RolePrincipal("user-member", "member");

        private static ClaimsPrincipal RolePrincipal(string userId, string role) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role),
            }, authenticationType: "pgwire"));

        /// <summary>
        /// A loopback pgwire session with the catalog responder registered, driven through
        /// a real cleartext handshake (under <paramref name="principal"/>) to ReadyForQuery.
        /// </summary>
        private sealed class PgSession : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _clientSocket;
            private readonly TcpClient _serverSocket;
            private readonly Task _serverTask;

            public PgHandshakeClient Client { get; }

            private PgSession(TcpListener listener, TcpClient clientSocket, TcpClient serverSocket, Task serverTask, PgHandshakeClient client)
            {
                _listener = listener;
                _clientSocket = clientSocket;
                _serverSocket = serverSocket;
                _serverTask = serverTask;
                Client = client;
            }

            public static async Task<PgSession> StartAsync(IQueryIntentExecutor executor, ClaimsPrincipal principal)
            {
                var services = new ServiceCollection()
                    .AddSingleton(executor)
                    .AddSingleton<IPgQueryTranslator, PgSubsetQueryTranslator>()
                    .AddSingleton<IPgCatalogResponder, PgCatalogResponder>()
                    .BuildServiceProvider();

                var store = new FakePgCredentialStore().Add("u", "pw", principal);
                var options = new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext };

                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                var clientSocket = new TcpClient();
                var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
                var serverSocket = await listener.AcceptTcpClientAsync();
                await connectTask;

                var handler = new PgConnectionHandler(store, BifrostAuthContextFactory.Instance, services, options);
                var serverTask = handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None);

                var client = new PgHandshakeClient(clientSocket.GetStream());
                await client.SendStartupAsync("u");
                await client.DoCleartextAsync("pw");
                var handshake = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);
                handshake.ReadyForQuery.Should().BeTrue("the handshake must reach a ready session before queries run");

                return new PgSession(listener, clientSocket, serverSocket, serverTask, client);
            }

            public async ValueTask DisposeAsync()
            {
                _clientSocket.Dispose();
                try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* teardown races on dispose are expected */ }
                _serverSocket.Dispose();
                _listener.Stop();
            }
        }
    }
}

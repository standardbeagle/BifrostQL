using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Simple query ('Q') protocol tests for pgwire slice 2, driven end to end over a
    /// loopback socket through a real authenticated handshake. The read boundary
    /// (<see cref="IQueryIntentExecutor"/>) is mocked so the tests pin the wire behavior —
    /// RowDescription/DataRow/CommandComplete encoding, pg type mapping, error mapping,
    /// autocommit resilience, and that the authenticated identity reaches the seam — while
    /// the real <see cref="PgSubsetQueryTranslator"/> and encoders run unmocked.
    /// </summary>
    public sealed class PgSimpleQueryTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Fact]
        public async Task Select_RoundTripsTypedRows_WithCorrectOidsAndValues()
        {
            // Arrange: a users table (int id, varchar name, bit active) with two rows,
            // one carrying a NULL name.
            var rows = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
                new Dictionary<string, object?> { ["id"] = 2, ["name"] = null, ["active"] = false },
            };
            var executor = FakeExecutor(UsersTable(), rows, out _);
            await using var fixture = await PgSession.StartAsync(executor);

            // Act
            await fixture.Client.SendQueryAsync("SELECT * FROM users");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            // Assert: RowDescription carries the field names and mapped pg type OIDs.
            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Equal("id", "name", "active");
            result.Fields.Select(f => f.TypeOid).Should().Equal(
                PgTypeMap.OidInt4, PgTypeMap.OidVarchar, PgTypeMap.OidBool);
            result.Fields.Should().OnlyContain(f => f.FormatCode == PgWireProtocol.FormatText);

            // DataRows decode to the expected text; bool is pg 't'/'f', NULL is a real null.
            result.Rows.Should().HaveCount(2);
            result.Rows[0].Should().Equal("1", "alice", "t");
            result.Rows[1].Should().Equal("2", null, "f");

            result.CommandTag.Should().Be("SELECT 2");
            result.TransactionStatus.Should().Be('I'); // autocommit idle
        }

        [Fact]
        public async Task Select_MultipleRows_ReportsCommandCompleteCountAndIdleReadyForQuery()
        {
            var rows = Enumerable.Range(1, 5)
                .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["id"] = i,
                    ["name"] = $"user{i}",
                    ["active"] = true,
                })
                .ToArray();
            var executor = FakeExecutor(UsersTable(), rows, out _);
            await using var fixture = await PgSession.StartAsync(executor);

            await fixture.Client.SendQueryAsync("SELECT id, name FROM users");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Rows.Should().HaveCount(5);
            result.CommandTag.Should().Be("SELECT 5");
            result.TransactionStatus.Should().Be('I');
        }

        [Fact]
        public async Task EmptyResultSet_StillSendsRowDescriptionAndSelectZero()
        {
            var executor = FakeExecutor(UsersTable(), Array.Empty<IReadOnlyDictionary<string, object?>>(), out _);
            await using var fixture = await PgSession.StartAsync(executor);

            await fixture.Client.SendQueryAsync("SELECT id, name, active FROM users");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Equal("id", "name", "active");
            result.Rows.Should().BeEmpty();
            result.CommandTag.Should().Be("SELECT 0");
        }

        [Fact]
        public async Task QueryError_SurfacesCleanPgError_AndSessionSurvivesForNextQuery()
        {
            var rows = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = 7, ["name"] = "carol", ["active"] = true },
            };
            var executor = FakeExecutor(UsersTable(), rows, out _);
            await using var fixture = await PgSession.StartAsync(executor);

            // Act 1: an unrecognized statement (slice 3 territory) → non-fatal syntax error.
            await fixture.Client.SendQueryAsync("DELETE FROM users WHERE id = 1");
            var errored = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            // Assert 1: clean pg ErrorResponse with a syntax SQLSTATE, back to idle.
            errored.HasError.Should().BeTrue();
            errored.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
            errored.TransactionStatus.Should().Be('I');

            // Act 2: the SAME connection accepts a subsequent valid query (autocommit resilience).
            await fixture.Client.SendQueryAsync("SELECT id FROM users");
            var ok = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            // Assert 2: it round-trips, proving the error did not tear the connection down.
            ok.HasError.Should().BeFalse();
            ok.Rows.Should().HaveCount(1);
            ok.Rows[0].Should().Equal("7");
            ok.CommandTag.Should().Be("SELECT 1");
        }

        [Fact]
        public async Task Read_RunsUnderAuthenticatedIdentity_TransformerSeamReceivesTenantContext()
        {
            var rows = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
            };
            var executor = FakeExecutor(UsersTable(), rows, out var captured);
            await using var fixture = await PgSession.StartAsync(executor);

            await fixture.Client.SendQueryAsync("SELECT * FROM users");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            // The intent that reached the read seam carries the projected identity — the
            // security transformer pipeline reads this. A missing/empty context would mean
            // the read bypassed identity (fail-open); it must carry the caller's tenant.
            captured.Intent.Should().NotBeNull();
            captured.Intent!.UserContext.Should().NotBeEmpty();
            captured.Intent.UserContext.Should()
                .ContainKey(MetadataKeys.Auth.DefaultTenantContextKey)
                .WhoseValue.Should().Be("tenant-a");
        }

        [Fact]
        public async Task ExecutionThrowsSensitiveText_WireMessageIsGenericSanitized_NotRawDetail_AndSessionSurvives()
        {
            // A BifrostExecutionError whose message wraps raw DB detail is the exact leak
            // vector the review advisory flagged: FromDatabaseException/ConnectionFailed can
            // embed driver/schema text, so its Message is NOT provably client-safe. It must
            // be sanitized to a generic internal_error, never forwarded verbatim.
            const string SensitiveText = "column secret_x does not exist in table users";
            var executor = ThrowingExecutor(UsersTable(),
                new BifrostExecutionError($"Database error: {SensitiveText}"));
            await using var fixture = await PgSession.StartAsync(executor);

            // Act 1: a well-formed SELECT that translates fine but throws at execution.
            await fixture.Client.SendQueryAsync("SELECT id FROM users");
            var errored = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            // Assert 1: internal_error SQLSTATE, generic wire message, NO sensitive substring.
            errored.HasError.Should().BeTrue();
            errored.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInternalError);
            errored.ErrorMessage.Should().Be(PgWireProtocol.InternalQueryErrorMessage);
            errored.ErrorMessage.Should().NotContain("secret_x");
            errored.ErrorMessage.Should().NotContain(SensitiveText);
            errored.TransactionStatus.Should().Be('I'); // non-fatal, still autocommit idle

            // Act 2: the SAME connection still serves a translation error cleanly, proving
            // the sanitized error did not tear the connection down.
            await fixture.Client.SendQueryAsync("DELETE FROM users WHERE id = 1");
            var next = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);
            next.HasError.Should().BeTrue();
            next.TransactionStatus.Should().Be('I');
        }

        [Fact]
        public async Task UserFacingTranslationError_ForwardsCuratedMessage_WithSyntaxSqlState()
        {
            // A deliberately user-facing query error (unknown relation) must NOT be
            // over-sanitized: the translator's curated message reaches the client so callers
            // can see what was wrong, with the syntax_error SQLSTATE.
            var executor = FakeExecutor(UsersTable(), Array.Empty<IReadOnlyDictionary<string, object?>>(), out _);
            await using var fixture = await PgSession.StartAsync(executor);

            await fixture.Client.SendQueryAsync("SELECT * FROM nonexistent");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
            result.ErrorMessage.Should().Contain("nonexistent"); // curated detail survives
            result.ErrorMessage.Should().NotBe(PgWireProtocol.InternalQueryErrorMessage);
            result.TransactionStatus.Should().Be('I');
        }

        [Fact]
        public async Task WhereQuery_CarriesFilterToTheReadSeam_AndRoundTrips()
        {
            var rows = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
            };
            var executor = FakeExecutor(UsersTable(), rows, out var captured);
            await using var fixture = await PgSession.StartAsync(executor);

            await fixture.Client.SendQueryAsync("SELECT id, name FROM users WHERE id > 5 AND name = 'alice'");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Equal("id", "name");
            // The parsed WHERE must reach the intent as a filter the security pipeline
            // extends — a null filter would mean the predicate was silently dropped.
            captured.Intent!.Query.Filter.Should().NotBeNull();
        }

        [Fact]
        public async Task NegativeLiteralWhere_RoundTrips_CarryingSignedFilter()
        {
            var rows = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = -3, ["name"] = "neg", ["active"] = true },
            };
            var executor = FakeExecutor(UsersTable(), rows, out var captured);
            await using var fixture = await PgSession.StartAsync(executor);

            // A negative numeric literal in WHERE must parse and reach the read seam as a
            // bound filter (not a rejected '-') so the correct rows come back.
            await fixture.Client.SendQueryAsync("SELECT id, name FROM users WHERE id = -3");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            captured.Intent!.Query.Filter.Should().NotBeNull();
            result.Rows.Should().ContainSingle().Which.Should().Equal("-3", "neg");
        }

        [Fact]
        public async Task OversizedNumericLiteral_ReturnsSyntaxError_NotInternalError_AndSessionSurvives()
        {
            const string oversized = "99999999999999999999999999999";
            var executor = FakeExecutor(UsersTable(),
                new IReadOnlyDictionary<string, object?>[]
                {
                    new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
                }, out _);
            await using var fixture = await PgSession.StartAsync(executor);

            // An out-of-range literal is client input error: clean 42601, never the
            // internal_error an escaped OverflowException would have produced, and it must
            // not tear the connection down or echo the raw value.
            await fixture.Client.SendQueryAsync($"SELECT id FROM users WHERE id = {oversized}");
            var errored = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            errored.HasError.Should().BeTrue();
            errored.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
            errored.ErrorSqlState.Should().NotBe(PgWireProtocol.SqlStateInternalError);
            errored.ErrorMessage.Should().NotContain(oversized);
            errored.ErrorMessage.Should().NotBe(PgWireProtocol.InternalQueryErrorMessage);
            errored.TransactionStatus.Should().Be('I');

            // The SAME connection still serves a subsequent valid query.
            await fixture.Client.SendQueryAsync("SELECT id FROM users");
            var ok = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);
            ok.HasError.Should().BeFalse();
        }

        [Fact]
        public async Task OutOfSubsetFeature_ReturnsFeatureNotSupported_AndSessionSurvives()
        {
            var executor = FakeExecutor(UsersTable(),
                new IReadOnlyDictionary<string, object?>[]
                {
                    new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
                }, out _);
            await using var fixture = await PgSession.StartAsync(executor);

            // GROUP BY is recognized but out of subset → feature_not_supported (not syntax).
            await fixture.Client.SendQueryAsync("SELECT id FROM users GROUP BY id");
            var errored = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            errored.HasError.Should().BeTrue();
            errored.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateFeatureNotSupported);
            errored.TransactionStatus.Should().Be('I');

            // The connection stays usable afterward.
            await fixture.Client.SendQueryAsync("SELECT id FROM users");
            var ok = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);
            ok.HasError.Should().BeFalse();
        }

        [Fact]
        public async Task Join_ProjectsQualifiedJoinedColumns_OverTheWire()
        {
            // The executor is mocked, so it returns the row the Core flatten would
            // produce (root scalar + the table-qualified joined column). This pins the
            // wire projection of a joined column; the flatten itself is proven in the
            // Core QueryIntentJoinFlattenTests against a real database.
            var rows = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["id"] = 10, ["users.name"] = "alice" },
            };
            var executor = FakeExecutor(OrdersModel(), rows, out var captured);
            await using var fixture = await PgSession.StartAsync(executor);

            await fixture.Client.SendQueryAsync(
                "SELECT o.id, u.name FROM orders o JOIN users u ON o.user_id = u.id");
            var result = await fixture.Client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Equal("id", "users.name");
            result.Rows.Should().ContainSingle().Which.Should().Equal("10", "alice");
            captured.Intent!.Query.Links.Should().HaveCount(1);
        }

        // ---- fixtures / doubles --------------------------------------------

        /// <summary>Captures the intent that reached the read seam.</summary>
        private sealed class CapturedIntent { public QueryIntent? Intent { get; set; } }

        /// <summary>
        /// An executor that translates (GetModelAsync resolves the table) but throws
        /// <paramref name="toThrow"/> at execution, exercising the query-phase error path.
        /// </summary>
        private static IQueryIntentExecutor ThrowingExecutor(IDbTable table, Exception toThrow)
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { table });

            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult<IDbModel>(model));
            executor.ExecuteAsync(Arg.Any<QueryIntent>(), Arg.Any<CancellationToken>())
                .Returns<Task<QueryIntentResult>>(_ => throw toThrow);
            return executor;
        }

        private static IQueryIntentExecutor FakeExecutor(
            IDbTable table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, out CapturedIntent captured)
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { table });

            var capture = new CapturedIntent();
            captured = capture;

            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult<IDbModel>(model));
            executor.ExecuteAsync(Arg.Do<QueryIntent>(i => capture.Intent = i), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new QueryIntentResult { Rows = rows, Sql = "-- mocked" }));
            return executor;
        }

        /// <summary>Executor over a prebuilt multi-table model (for join queries).</summary>
        private static IQueryIntentExecutor FakeExecutor(
            IDbModel model, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, out CapturedIntent captured)
        {
            var capture = new CapturedIntent();
            captured = capture;

            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            executor.ExecuteAsync(Arg.Do<QueryIntent>(i => capture.Intent = i), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new QueryIntentResult { Rows = rows, Sql = "-- mocked" }));
            return executor;
        }

        /// <summary>orders (FK user_id → users.id) + users, with the forward single-link.</summary>
        private static IDbModel OrdersModel()
        {
            var users = UsersTable();
            var orders = Substitute.For<IDbTable>();
            orders.DbName.Returns("orders");
            orders.GraphQlName.Returns("orders");
            orders.TableSchema.Returns("dbo");
            var orderCols = new[]
            {
                new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
                new ColumnDto { ColumnName = "user_id", GraphQlName = "user_id", DataType = "int", OrdinalPosition = 2 },
            };
            orders.Columns.Returns(orderCols);
            orders.MultiLinks.Returns(new Dictionary<string, TableLinkDto>());

            // Materialize the link (which reads users.Columns) before configuring the
            // substitute call, so NSubstitute doesn't see a nested configuration.
            var usersId = users.Columns.First(c => c.ColumnName == "id");
            var link = new TableLinkDto
            {
                Name = "users",
                ParentTable = users,
                ChildTable = orders,
                ParentId = usersId,
                ChildId = orderCols.First(c => c.ColumnName == "user_id"),
            };
            orders.SingleLinks.Returns(new Dictionary<string, TableLinkDto> { ["users"] = link });

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { orders, users });
            return model;
        }

        private static IDbTable UsersTable()
        {
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns("users");
            table.GraphQlName.Returns("users");
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[]
            {
                new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
                new ColumnDto { ColumnName = "name", GraphQlName = "name", DataType = "varchar", OrdinalPosition = 2, IsNullable = true },
                new ColumnDto { ColumnName = "active", GraphQlName = "active", DataType = "bit", OrdinalPosition = 3 },
            });
            return table;
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "pgwire"));

        /// <summary>
        /// A loopback pgwire session driven through a real cleartext handshake to
        /// ReadyForQuery, with the given query executor registered and the real simple
        /// query translator. Leaves the client positioned to send queries.
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

            public static async Task<PgSession> StartAsync(IQueryIntentExecutor executor)
            {
                var services = new ServiceCollection()
                    .AddSingleton(executor)
                    .AddSingleton<IPgQueryTranslator, PgSubsetQueryTranslator>()
                    .BuildServiceProvider();

                var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
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
                await client.SendStartupAsync("alice");
                await client.DoCleartextAsync("s3cret");
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

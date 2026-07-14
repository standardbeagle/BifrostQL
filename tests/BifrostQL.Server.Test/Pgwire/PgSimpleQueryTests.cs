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
    /// the real <see cref="PgSimpleQueryTranslator"/> and encoders run unmocked.
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

        // ---- fixtures / doubles --------------------------------------------

        /// <summary>Captures the intent that reached the read seam.</summary>
        private sealed class CapturedIntent { public QueryIntent? Intent { get; set; } }

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
                    .AddSingleton<IPgQueryTranslator, PgSimpleQueryTranslator>()
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

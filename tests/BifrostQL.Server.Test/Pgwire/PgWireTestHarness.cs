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

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// A loopback pgwire front door for slice-5 tests. Faithful to production: ONE shared
    /// <see cref="PgConnectionHandler"/> (with one <see cref="PgCancellationRegistry"/> and one
    /// <see cref="PgConnectionLimiter"/>) services every accepted connection via a background
    /// accept loop — so CancelRequests arriving on a SEPARATE socket are genuinely accepted and
    /// processed, and connection-limit admission is enforced across connections, exactly as the
    /// Kestrel singleton handler would behave.
    /// </summary>
    internal sealed class PgWireTestHarness : IAsyncDisposable
    {
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

        private readonly TcpListener _listener;
        private readonly PgConnectionHandler _handler;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;
        private readonly List<TcpClient> _clientSockets = new();
        private readonly List<TcpClient> _serverSockets = new();
        private readonly List<Task> _serverTasks = new();

        public IPEndPoint Endpoint { get; }
        public PgCancellationRegistry Registry { get; }
        public PgConnectionLimiter Limiter { get; }

        public PgWireTestHarness(IQueryIntentExecutor executor, int maxConnections = 100)
        {
            var options = new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, MaxConnections = maxConnections };
            Registry = new PgCancellationRegistry();
            Limiter = new PgConnectionLimiter(maxConnections);
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));

            var services = new ServiceCollection()
                .AddSingleton(executor)
                .AddSingleton<IPgQueryTranslator, PgSubsetQueryTranslator>()
                .AddSingleton<IPgCatalogResponder, PgCatalogResponder>()
                .BuildServiceProvider();

            _handler = new PgConnectionHandler(store, BifrostAuthContextFactory.Instance, services, options, Registry, Limiter);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Endpoint = (IPEndPoint)_listener.LocalEndpoint;
            _acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await _listener.AcceptTcpClientAsync(_shutdown.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                lock (_serverSockets)
                {
                    _serverSockets.Add(socket);
                    _serverTasks.Add(_handler.HandleConnectionAsync(socket.GetStream(), CancellationToken.None));
                }
            }
        }

        /// <summary>Connects a raw client (no handshake) so admission rejection can be observed.</summary>
        public async Task<PgSessionHandle> ConnectAsync()
        {
            var clientSocket = new TcpClient();
            await clientSocket.ConnectAsync(Endpoint.Address, Endpoint.Port);
            lock (_clientSockets) _clientSockets.Add(clientSocket);
            return new PgSessionHandle(clientSocket, new PgHandshakeClient(clientSocket.GetStream()));
        }

        /// <summary>Connects and drives a full cleartext handshake to a ready session.</summary>
        public async Task<PgSessionHandle> OpenSessionAsync()
        {
            var handle = await ConnectAsync();
            await handle.Client.SendStartupAsync("alice");
            await handle.Client.DoCleartextAsync("s3cret");
            var handshake = await handle.Client.WaitForReadyOrErrorAsync().WaitAsync(HandshakeTimeout);
            handshake.ReadyForQuery.Should().BeTrue("the handshake must reach a ready session before queries run");
            return handle;
        }

        /// <summary>Polls the admission counter until it reaches <paramref name="expected"/> or times out.</summary>
        public async Task WaitForConnectionCountAsync(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Limiter.Count != expected && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Limiter.Count.Should().Be(expected);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            lock (_clientSockets) foreach (var s in _clientSockets) s.Dispose();
            try { await _acceptLoop; } catch { /* accept loop shutdown races are expected */ }
            lock (_serverSockets)
            {
                foreach (var s in _serverSockets) s.Dispose();
            }
            _listener.Stop();
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "pgwire"));

        // ---- shared doubles --------------------------------------------------

        public sealed class CapturedIntent { public QueryIntent? Intent { get; set; } public int ExecuteCount { get; set; } }

        /// <summary>Executor over a single users table (int id, varchar name, bit active).</summary>
        public static IQueryIntentExecutor UsersExecutor(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, out CapturedIntent captured)
        {
            var table = UsersTable(); // materialize before configuring model (avoid nested NSubstitute)
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { table });

            var capture = new CapturedIntent();
            captured = capture;

            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            executor.ExecuteAsync(Arg.Do<QueryIntent>(i => { capture.Intent = i; capture.ExecuteCount++; }), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new QueryIntentResult { Rows = rows, Sql = "-- mocked" }));
            return executor;
        }

        /// <summary>
        /// Executor whose ExecuteAsync blocks until its CancellationToken fires, then throws
        /// OperationCanceledException — the shape a cooperative in-flight query has. Signals
        /// <paramref name="started"/> once execution is underway so a test can time a
        /// CancelRequest, and never completes on its own (so only a cancel resolves it).
        /// </summary>
        public static IQueryIntentExecutor BlockingExecutor(TaskCompletionSource started)
        {
            var table = UsersTable(); // materialize before configuring model (avoid nested NSubstitute)
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { table });

            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            executor.ExecuteAsync(Arg.Any<QueryIntent>(), Arg.Any<CancellationToken>())
                .Returns<Task<QueryIntentResult>>(async call =>
                {
                    var token = call.Arg<CancellationToken>();
                    started.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.Infinite, token); // resolves only via cancellation
                    throw new OperationCanceledException(token);
                });
            return executor;
        }

        public static IDbTable UsersTable()
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

        /// <summary>Recursively collects every bound value carried by a TableFilter tree.</summary>
        public static List<object?> CollectFilterValues(TableFilter? filter)
        {
            var values = new List<object?>();
            void Walk(TableFilter? node)
            {
                if (node is null) return;
                values.Add(node.Value);
                Walk(node.Next);
                foreach (var child in node.And) Walk(child);
                foreach (var child in node.Or) Walk(child);
            }
            Walk(filter);
            return values;
        }
    }

    /// <summary>One client-side session: the driver plus the client socket that owns the slot.</summary>
    internal sealed class PgSessionHandle : IAsyncDisposable
    {
        private readonly TcpClient _clientSocket;
        public PgHandshakeClient Client { get; }

        public PgSessionHandle(TcpClient clientSocket, PgHandshakeClient client)
        {
            _clientSocket = clientSocket;
            Client = client;
        }

        /// <summary>Closes the client end, which releases the server's connection slot.</summary>
        public ValueTask DisposeAsync()
        {
            _clientSocket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

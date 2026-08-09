using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BifrostQL.Server;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Resource caps on the RESP listener. It previously had NONE: no connection limit and no read
    /// deadline, so any peer could exhaust sockets, threads and memory with no credentials at all.
    /// Both caps are exercised through ONE shared handler and limiter driving several real sockets,
    /// as the Kestrel singleton handler behaves — a per-connection handler could not observe a
    /// cross-connection cap at all.
    /// </summary>
    public sealed class RespConnectionLimitTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Fact]
        public async Task OverTheLimit_ConnectionIsRefused_BeforeItSendsAnything_ThenAFreedSlotIsReusable()
        {
            await using var harness = new RespLimitHarness(maxConnections: 2);

            var first = await harness.ConnectAsync();
            var second = await harness.ConnectAsync();
            await harness.WaitForConnectionCountAsync(2);

            // The third peer sends NOTHING — no AUTH, no PING, not a byte — and is still refused.
            // The cap has to bind at ACCEPT: a cap applied after the codec has read would let an
            // unauthenticated peer force decode work outside the limit, which is not a cap at all.
            var third = await harness.ConnectAsync();
            var refused = await third.ReadReplyAsync().WaitAsync(Timeout);
            refused.Should().BeOfType<RespError>()
                .Which.Message.Should().Be(RespProtocol.TooManyConnectionsError);

            // Freeing a slot makes the front door usable again.
            await first.DisposeAsync();
            await harness.WaitForConnectionCountAsync(1);

            var revived = await harness.ConnectAsync();
            await revived.SendCommandAsync("PING");
            // RequireAuthentication is on, so PING answers NOAUTH — proof the connection was
            // ADMITTED and reached the dispatch loop rather than being refused at the door.
            var reply = await revived.ReadReplyAsync().WaitAsync(Timeout);
            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.NoAuthError);

            await second.DisposeAsync();
            await revived.DisposeAsync();
            await third.DisposeAsync();
        }

        [Fact]
        public async Task StalledUnauthenticatedConnection_IsDroppedByTheAuthDeadline_AndReleasesItsSlot()
        {
            await using var harness = new RespLimitHarness(
                maxConnections: 1, authenticationTimeout: TimeSpan.FromMilliseconds(400));

            // A peer that connects and never authenticates. With the slot taken at accept and no
            // deadline, this one silent socket owns the entire front door forever.
            var staller = await harness.ConnectAsync();
            await harness.WaitForConnectionCountAsync(1);

            await harness.WaitForConnectionCountAsync(0);

            var revived = await harness.ConnectAsync();
            await revived.SendCommandAsync("PING");
            (await revived.ReadReplyAsync().WaitAsync(Timeout)).Should().BeOfType<RespError>();

            await staller.DisposeAsync();
            await revived.DisposeAsync();
        }

        /// <summary>
        /// One RespConnectionHandler and one RespConnectionLimiter behind an accept loop, so the
        /// admission cap is genuinely enforced ACROSS connections.
        /// </summary>
        private sealed class RespLimitHarness : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly RespConnectionHandler _handler;
            private readonly CancellationTokenSource _shutdown = new();
            private readonly Task _acceptLoop;
            private readonly List<TcpClient> _serverSockets = new();

            public RespConnectionLimiter Limiter { get; }

            public RespLimitHarness(int maxConnections, TimeSpan? authenticationTimeout = null)
            {
                var options = new RespWireOptions
                {
                    MaxConnections = maxConnections,
                    AuthenticationTimeout = authenticationTimeout ?? TimeSpan.FromSeconds(30),
                };
                Limiter = new RespConnectionLimiter(maxConnections);
                var store = new FakeRespCredentialStore()
                    .Add("alice", "s3cret", new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "user-alice") }, "resp")));

                _handler = new RespConnectionHandler(
                    store, BifrostAuthContextFactory.Instance,
                    new ServiceCollection().BuildServiceProvider(), options,
                    dataHandlers: null, logger: null, connectionLimiter: Limiter);

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
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
                    lock (_serverSockets) _serverSockets.Add(socket);
                    _ = Task.Run(async () =>
                    {
                        try { await _handler.HandleConnectionAsync(socket.GetStream(), CancellationToken.None); }
                        finally { socket.Close(); }
                    });
                }
            }

            public async Task<RespConnectionHandle> ConnectAsync()
            {
                var endpoint = (IPEndPoint)_listener.LocalEndpoint;
                var client = new TcpClient();
                await client.ConnectAsync(endpoint.Address, endpoint.Port);
                return new RespConnectionHandle(client);
            }

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
                try { await _acceptLoop; } catch { /* shutdown races are expected */ }
                lock (_serverSockets) foreach (var s in _serverSockets) s.Dispose();
                _listener.Stop();
            }
        }

        private sealed class RespConnectionHandle : IAsyncDisposable
        {
            private readonly TcpClient _socket;
            private readonly RespTestClient _client;

            public RespConnectionHandle(TcpClient socket)
            {
                _socket = socket;
                _client = new RespTestClient(socket.GetStream());
            }

            public Task SendCommandAsync(params string[] arguments) => _client.SendCommandAsync(arguments);
            public Task<RespValue?> ReadReplyAsync() => _client.ReadReplyAsync();

            public ValueTask DisposeAsync()
            {
                _socket.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

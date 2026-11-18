using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// A declared length near <c>int.MaxValue</c> must be refused as a protocol violation, never
    /// slip past an <c>offset + length &gt; body.Length</c> check that wraps negative. The wrapped
    /// check let the decode throw <see cref="ArgumentOutOfRangeException"/>, which the connection
    /// handler's catch filter does not cover, so the session task faulted with no ErrorResponse —
    /// before authentication (SASLInitialResponse) and after it (Bind value length). Invariant 1.
    /// </summary>
    public sealed class PgMalformedLengthTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Fact]
        public async Task PreAuth_SaslInitialResponse_with_wrapping_length_answers_protocol_violation()
        {
            await using var fixture = await Fixture.StartAsync(PgAuthMethod.ScramSha256);
            var client = fixture.Client;

            await client.SendStartupAsync("alice");
            // Declares int.MaxValue client-first bytes but carries four: offset + length wraps.
            await client.SendSaslInitialWithDeclaredLengthAsync(int.MaxValue, Encoding.ASCII.GetBytes("n,,x"));
            var result = await fixture.ReadUnlessHandlerFaultedAsync(client.WaitForReadyOrErrorAsync());

            result.ReadyForQuery.Should().BeFalse();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation);
            await fixture.AssertServerEndedCleanlyAsync();
        }

        [Fact]
        public async Task Bind_value_with_wrapping_length_answers_protocol_violation()
        {
            await using var fixture = await Fixture.StartAsync(PgAuthMethod.Cleartext);
            var client = fixture.Client;
            await client.SendStartupAsync("alice");
            await client.DoCleartextAsync("s3cret");
            (await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout)).ReadyForQuery.Should().BeTrue();

            await client.SendParseAsync("", "SELECT id FROM users WHERE id = $1");
            // Bind: portal "", statement "", no format codes, one value declaring int.MaxValue bytes
            // while carrying one.
            using var body = new MemoryStream();
            body.WriteByte(0); body.WriteByte(0);
            WriteInt16(body, 0);
            WriteInt16(body, 1);
            WriteInt32(body, int.MaxValue);
            body.WriteByte((byte)'1');
            WriteInt16(body, 0);
            await client.SendRawFrontendAsync(PgWireProtocol.BindMessage, body.ToArray());

            // ParseComplete, then the FATAL protocol_violation for the malformed Bind.
            var (parseType, _) = await client.ReadNextMessageAsync().WaitAsync(Timeout);
            parseType.Should().Be(PgWireProtocol.ParseComplete);
            var (type, errorBody) = await fixture.ReadUnlessHandlerFaultedAsync(client.ReadNextMessageAsync());
            PgHandshakeClient.ErrorSqlStateOf(type, errorBody).Should().Be(PgWireProtocol.SqlStateProtocolViolation);
            await fixture.AssertServerEndedCleanlyAsync();
        }

        private static void WriteInt16(Stream s, short v)
        {
            var b = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteInt32(Stream s, int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            s.Write(b);
        }

        /// <summary>One loopback connection whose server task stays observable, so an exception
        /// escaping the handler shows up as a faulted task rather than a silent close.</summary>
        private sealed class Fixture : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _clientSocket;
            private readonly TcpClient _serverSocket;
            private readonly Task _serverTask;

            public PgHandshakeClient Client { get; }

            private Fixture(TcpListener listener, TcpClient clientSocket, TcpClient serverSocket, Task serverTask)
            {
                _listener = listener;
                _clientSocket = clientSocket;
                _serverSocket = serverSocket;
                _serverTask = serverTask;
                Client = new PgHandshakeClient(clientSocket.GetStream());
            }

            public static async Task<Fixture> StartAsync(PgAuthMethod method)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var clientSocket = new TcpClient();
                var connect = clientSocket.ConnectAsync(IPAddress.Loopback, port);
                var serverSocket = await listener.AcceptTcpClientAsync();
                await connect;

                var executor = PgWireTestHarness.UsersExecutor(Array.Empty<IReadOnlyDictionary<string, object?>>(), out _);
                var services = new ServiceCollection()
                    .AddSingleton(executor)
                    .AddSingleton<IPgQueryTranslator, PgSubsetQueryTranslator>()
                    .BuildServiceProvider();
                var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-alice"),
                    new Claim(LocalAuthClaims.Tenant, "tenant-a"),
                }, authenticationType: "pgwire"));
                var store = new FakePgCredentialStore().Add("alice", "s3cret", principal);
                var handler = new PgConnectionHandler(store, BifrostAuthContextFactory.Instance, services,
                    new PgWireOptions { AuthMethod = method, AllowCleartextPasswordWithoutTls = true });
                var serverTask = handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None);
                return new Fixture(listener, clientSocket, serverSocket, serverTask);
            }

            /// <summary>
            /// Awaits <paramref name="read"/>, but first fails with the escaped exception's type if
            /// the handler task faults before the reply arrives — the bug's own signature, rather
            /// than a read timeout on a socket the faulted handler never closed.
            /// </summary>
            public async Task<T> ReadUnlessHandlerFaultedAsync<T>(Task<T> read)
            {
                var first = await Task.WhenAny(read, _serverTask, Task.Delay(Timeout));
                if (ReferenceEquals(first, _serverTask))
                    _serverTask.IsFaulted.Should().BeFalse(
                        $"no exception may escape the connection handler (invariant 1); got {_serverTask.Exception?.GetBaseException().GetType().Name}");
                return await read.WaitAsync(Timeout);
            }

            /// <summary>The handler must finish on its own (it closes after the FATAL) and must not fault.</summary>
            public async Task AssertServerEndedCleanlyAsync()
            {
                var ended = await Task.WhenAny(_serverTask, Task.Delay(Timeout));
                ended.Should().BeSameAs(_serverTask, "the handler must close the session after a FATAL protocol violation");
                _serverTask.IsFaulted.Should().BeFalse(
                    $"no exception may escape the connection handler (invariant 1); got {_serverTask.Exception?.GetBaseException().GetType().Name}");
            }

            public async ValueTask DisposeAsync()
            {
                _clientSocket.Dispose();
                try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* teardown races are expected */ }
                _serverSocket.Dispose();
                _listener.Stop();
            }
        }
    }
}

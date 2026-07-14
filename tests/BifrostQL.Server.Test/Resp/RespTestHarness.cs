using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BifrostQL.Server.Resp;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>A test credential store: an in-memory username → (secret, principal) map.</summary>
    internal sealed class FakeRespCredentialStore : IRespCredentialStore
    {
        private readonly Dictionary<string, RespLogin> _logins = new(StringComparer.Ordinal);

        public FakeRespCredentialStore Add(string username, string secret, ClaimsPrincipal principal)
        {
            _logins[username] = new RespLogin(secret, principal);
            return this;
        }

        public Task<RespLogin?> FindAsync(string username, CancellationToken cancellationToken)
            => Task.FromResult(_logins.TryGetValue(username, out var login) ? login : null);
    }

    /// <summary>
    /// A hand-written RESP frontend that drives the connection handler over a stream exactly
    /// as redis-cli / a Redis client would: commands as arrays of bulk strings, replies decoded
    /// with the real codec. Independent of the server's writer path (it re-uses only the reader),
    /// so it genuinely exercises the wire.
    /// </summary>
    internal sealed class RespTestClient
    {
        private readonly Stream _stream;
        private readonly RespReader _reader;

        public RespTestClient(Stream stream)
        {
            _stream = stream;
            _reader = new RespReader(stream, 1 << 20, 1 << 20, 32);
        }

        public async Task SendCommandAsync(params string[] arguments)
        {
            var array = new RespArray(arguments.Select(a => (RespValue)RespValue.Bulk(a)).ToList());
            await RespWriter.WriteAsync(_stream, array, default);
        }

        public async Task SendRawAsync(byte[] bytes)
        {
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public Task<RespValue?> ReadReplyAsync() => _reader.ReadValueAsync(default);
    }

    /// <summary>Loopback socket pair with a <see cref="RespConnectionHandler"/> pumping the server end.</summary>
    internal sealed class RespFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _clientSocket;
        private readonly TcpClient _serverSocket;
        private readonly Task _serverTask;

        public RespTestClient Client { get; }

        private RespFixture(TcpListener listener, TcpClient clientSocket, TcpClient serverSocket, Task serverTask)
        {
            _listener = listener;
            _clientSocket = clientSocket;
            _serverSocket = serverSocket;
            _serverTask = serverTask;
            Client = new RespTestClient(clientSocket.GetStream());
        }

        public static async Task<RespFixture> StartAsync(
            IRespCredentialStore store, IServiceProvider services, RespWireOptions options)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var clientSocket = new TcpClient();
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await listener.AcceptTcpClientAsync();
            await connectTask;

            var handler = new RespConnectionHandler(store, BifrostAuthContextFactory.Instance, services, options);
            // Close the server socket when the handler returns (QUIT / protocol-error / EOF), exactly
            // as Kestrel closes the connection when OnConnectedAsync returns — so a client blocked on a
            // read observes EOF instead of hanging.
            var serverTask = Task.Run(async () =>
            {
                try { await handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None); }
                finally { serverSocket.Close(); }
            });
            return new RespFixture(listener, clientSocket, serverSocket, serverTask);
        }

        public static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

        public async ValueTask DisposeAsync()
        {
            _clientSocket.Dispose();
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* connection teardown races are expected on dispose */ }
            _serverSocket.Dispose();
            _listener.Stop();
        }
    }
}

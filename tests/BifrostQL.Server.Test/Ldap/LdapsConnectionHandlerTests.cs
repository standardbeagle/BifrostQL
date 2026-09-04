using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Tests that enter through <see cref="LdapsConnectionHandler.OnConnectedAsync"/> — the Kestrel
    /// seam — so the PRODUCTION source-key derivation is under test, not a test-supplied string.
    /// </summary>
    public sealed class LdapsConnectionHandlerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private sealed class RecordingStore : ILdapCredentialStore
        {
            public readonly List<string> Lookups = new();

            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                Lookups.Add(bindDn);
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "alice") }, "ldap"));
                return Task.FromResult<LdapCredentialRecord?>(
                    new LdapCredentialRecord("hash:s3cret", principal, Enabled: true));
            }
        }

        private sealed class Hasher : ILdapPasswordHasher
        {
            public string DecoyHash => "hash:$decoy$";
            public bool Verify(ReadOnlySpan<byte> password, string passwordHash) =>
                passwordHash != DecoyHash && passwordHash == "hash:" + Encoding.UTF8.GetString(password);
        }

        private sealed class Factory : IBifrostAuthContextFactory
        {
            public IDictionary<string, object?> CreateUserContext(HttpContext context)
            {
                var sub = context.User.FindFirst(ClaimTypes.Name)?.Value;
                return string.IsNullOrEmpty(sub)
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["sub"] = sub };
            }

            public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
                => CreateUserContext(context);
        }

        private sealed class StreamDuplexPipe : System.IO.Pipelines.IDuplexPipe
        {
            public StreamDuplexPipe(Stream stream)
            {
                Input = System.IO.Pipelines.PipeReader.Create(stream);
                Output = System.IO.Pipelines.PipeWriter.Create(stream);
            }
            public System.IO.Pipelines.PipeReader Input { get; }
            public System.IO.Pipelines.PipeWriter Output { get; }
        }

        /// <summary>
        /// Opens a loopback TCP pair, pumps <paramref name="handler"/> on the server end through its
        /// Kestrel <c>OnConnectedAsync</c> entry point with a synthetic connection context carrying
        /// <paramref name="remote"/>, and completes the client half of the TLS handshake.
        /// </summary>
        private static async Task<(LdapTestClient Client, Func<Task> Cleanup)> StartLdapsAsync(
            LdapsConnectionHandler handler, IPEndPoint remote)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clientSocket = new TcpClient();
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await listener.AcceptTcpClientAsync();
            await connectTask;

            var context = new DefaultConnectionContext
            {
                Transport = new StreamDuplexPipe(serverSocket.GetStream()),
                RemoteEndPoint = remote,
            };
            var serverTask = handler.OnConnectedAsync(context);

            var client = new LdapTestClient(clientSocket.GetStream());
            await client.UpgradeToTlsAsync();

            return (client, async () =>
            {
                clientSocket.Dispose();
                try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* connection teardown races are expected on dispose */ }
                serverSocket.Dispose();
                listener.Stop();
            });
        }

        [Fact]
        public async Task Ldaps_PerSourceBindCap_KeysOnClientIp_NotTheEphemeralPort()
        {
            // The production entry point derives the throttle key from the Kestrel RemoteEndPoint.
            // Every reconnect carries a FRESH ephemeral port, and the attack this throttle exists
            // for IS a reconnect loop (one password guess per connection) — so a key of "ip:port"
            // makes the per-source cap per-connection and the guard never trips in production.
            // Three connections from ONE address on THREE ports: the third bind must be refused by
            // the limiter BEFORE the credential store is consulted.
            var options = new LdapWireOptions
            {
                ServerCertificate = LdapTestCertificate.Instance,
                MaxBindAttemptsPerSource = 2,
                BindRateLimitWindow = TimeSpan.FromMinutes(5),
            };
            var store = new RecordingStore();
            var authenticator = new LdapBindAuthenticator(store, new Hasher(), new Factory(), options);
            var limiter = new LdapBoundedCounter(options.MaxConnections, "MaxConnections");
            var tls = LdapTlsProvider.Create(options)!;
            var session = new LdapConnectionHandler(options, limiter, authenticator, tls);
            var handler = new LdapsConnectionHandler(options, limiter, tls, session);
            var address = IPAddress.Parse("203.0.113.7");

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (client, cleanup) = await StartLdapsAsync(handler, new IPEndPoint(address, 40000 + attempt));
                await client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "wrong")));
                var response = await client.ReadResponseAsync().WaitAsync(Timeout);
                response.Should().NotBeNull();
                response!.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
                await cleanup();
            }

            var (throttled, throttledCleanup) = await StartLdapsAsync(handler, new IPEndPoint(address, 40002));
            await throttled.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "wrong")));
            var refused = await throttled.ReadResponseAsync().WaitAsync(Timeout);
            refused.Should().NotBeNull();
            refused!.ResultCode.Should().Be(LdapResultCode.InvalidCredentials,
                "a rate-limited bind is refused with the same uniform invalidCredentials");
            store.Lookups.Should().HaveCount(2,
                "the per-source cap must be keyed on the client IP, not on the connection's ephemeral port — "
                + "a third reconnect from the same address is refused before the credential store runs");
            await throttledCleanup();
        }
    }
}

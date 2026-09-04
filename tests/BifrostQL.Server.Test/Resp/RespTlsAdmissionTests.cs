using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BifrostQL.Server;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The RESP admission slot must be reserved at ACCEPT — before the TLS handshake — not inside
    /// the connection handler, which Kestrel does not reach until the handshake completes. With the
    /// cap applied after the handshake, a peer that opens a socket and never sends a ClientHello
    /// costs a Kestrel connection, a TLS state machine and a read that the cap cannot see: the
    /// listener's declared MaxConnections bounds only sessions that got that far, which is not a cap
    /// on the resource an unauthenticated peer can force.
    ///
    /// <para>These run a REAL Kestrel listener with a real certificate: the ordering under test is
    /// the connection-middleware registration order, which no in-process harness can observe.</para>
    /// </summary>
    public sealed class RespTlsAdmissionTests
    {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task The_slot_is_taken_at_accept_even_when_the_peer_never_starts_the_tls_handshake()
        {
            using var certificate = SelfSigned();
            var (host, port) = await StartOnAFreePortAsync(certificate, maxConnections: 1);
            using var _ = host;
            var limiter = host.Services.GetRequiredService<RespConnectionLimiter>();

            // A silent peer: connected, not one byte sent, no ClientHello. This is the cheapest
            // possible attack against a TLS front door.
            using var silent = new TcpClient();
            await silent.ConnectAsync(IPAddress.Loopback, port);

            await WaitForCountAsync(limiter, 1);
            limiter.Count.Should().Be(1,
                "the slot must be reserved at accept — a cap that only counts post-handshake "
                + "connections cannot bound what an unauthenticated peer forces");

            // …and with the only slot held, the next peer is turned away at the door rather than
            // being handed a TLS handshake.
            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            var closed = await ReadUntilClosedAsync(second);
            closed.Should().BeTrue("the over-cap connection must be dropped without a handshake");
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>
        /// Binds a real listener, retrying on a lost port race: the free-port probe releases the
        /// port before Kestrel claims it, so a concurrently running test can take it in between.
        /// </summary>
        private static async Task<(IHost Host, int Port)> StartOnAFreePortAsync(
            X509Certificate2 certificate, int maxConnections)
        {
            for (var attempt = 1; ; attempt++)
            {
                var port = FreePort();
                try
                {
                    return (await StartAsync(port, certificate, maxConnections), port);
                }
                catch (IOException) when (attempt < 5)
                {
                    // Another listener claimed the probed port; try a different one.
                }
            }
        }

        private static async Task<IHost> StartAsync(int port, X509Certificate2 certificate, int maxConnections)
        {
            var store = new FakeRespCredentialStore().Add(
                "alice", "s3cret",
                new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-alice") }, "resp")));

            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseKestrel();
                web.UseUrls();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IRespCredentialStore>(store);
                    services.AddSingleton<IBifrostAuthContextFactory>(BifrostAuthContextFactory.Instance);
                    services.AddBifrostResp(o =>
                    {
                        o.Port = port;
                        o.MaxConnections = maxConnections;
                        o.ServerCertificate = certificate;
                    });
                });
                web.Configure(_ => { });
            });

            return await builder.StartAsync();
        }

        private static async Task WaitForCountAsync(RespConnectionLimiter limiter, int expected)
        {
            var deadline = DateTime.UtcNow + Settle;
            while (limiter.Count != expected && DateTime.UtcNow < deadline)
                await Task.Delay(25);
        }

        /// <summary>True when the peer closed the connection within the settle window.</summary>
        private static async Task<bool> ReadUntilClosedAsync(TcpClient client)
        {
            using var deadline = new CancellationTokenSource(Settle);
            var buffer = new byte[64];
            try
            {
                while (true)
                {
                    var read = await client.GetStream().ReadAsync(buffer, deadline.Token);
                    if (read == 0)
                        return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static X509Certificate2 SelfSigned()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=bifrost-resp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Re-import through PKCS#12 so the private key is usable by SslStream on every platform.
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        }
    }
}

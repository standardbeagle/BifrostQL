using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Drives the conformance kit's "admission slot is held before the TLS handshake" probe
    /// against a REAL listener.
    ///
    /// <para>The ordering under test is the listener's connection-middleware registration order,
    /// so no in-process stream harness can observe it: the probe has to bind a port and connect a
    /// socket. The shape is the same for every adapter — cap the listener at ONE connection, park
    /// a peer that sends nothing, and check both that it costs a slot and that the next peer is
    /// turned away without reaching a handshake. What differs per adapter is only how its host is
    /// started and where its counter lives, which the caller supplies.</para>
    /// </summary>
    internal static class ProtocolTlsAdmissionHarness
    {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

        /// <summary>Adapts an <see cref="Microsoft.Extensions.Hosting.IHost"/> to the probe's stop contract.</summary>
        public sealed class HostStopper : IAsyncDisposable
        {
            private readonly Microsoft.Extensions.Hosting.IHost _host;
            public HostStopper(Microsoft.Extensions.Hosting.IHost host) => _host = host;

            public async ValueTask DisposeAsync()
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _host.StopAsync(stop.Token);
                _host.Dispose();
            }
        }

        /// <param name="startListener">
        /// Binds this adapter's listener on the given port with a cap of ONE connection and the
        /// given certificate, returning something that stops it plus a reader for this front
        /// door's current admitted-connection count.
        /// </param>
        /// <param name="startHandshake">
        /// Attempts the adapter's TLS handshake as a client. Implicit-TLS front doors hand back an
        /// <c>AuthenticateAsClientAsync</c>; an adapter negotiating TLS in band (pgwire's
        /// SSLRequest) sends its negotiation packet first. Must return false rather than throw when
        /// the handshake does not complete.
        /// </param>
        public static async Task<(int SlotsHeldBySilentPeer, bool OverCapPeerClosed, bool OverCapPeerCompletedTlsHandshake)>
            ProbeAsync(
                Func<int, X509Certificate2, Task<(IAsyncDisposable Listener, Func<int> AdmittedCount)>> startListener,
                Func<TcpClient, Task<bool>> startHandshake)
        {
            using var certificate = SelfSigned();
            var (started, port) = await StartOnAFreePortAsync(startListener, certificate);
            await using var _ = started.Listener;
            var admittedCount = started.AdmittedCount;

            // A silent peer: connected, not one byte sent, no ClientHello. The cheapest possible
            // attack on a TLS front door — no credentials, no protocol knowledge, no work.
            using var silent = new TcpClient();
            await silent.ConnectAsync(IPAddress.Loopback, port);
            await WaitForCountAsync(admittedCount, 1);
            var slotsHeld = admittedCount();

            // …and with the only slot held, the next peer must be turned away at the door rather
            // than being handed a TLS state machine.
            using var overCap = new TcpClient();
            if (!await ConnectAllowingRefusalAtConnectAsync(overCap, port))
            {
                // Refused before the client's connect even completed. No handshake was offered,
                // which is precisely what both remaining assertions ask.
                return (slotsHeld, OverCapPeerClosed: true, OverCapPeerCompletedTlsHandshake: false);
            }

            var handshakeCompleted = await startHandshake(overCap);
            var closed = handshakeCompleted || await ReadUntilClosedAsync(overCap);

            return (slotsHeld, closed, handshakeCompleted);
        }

        /// <summary>
        /// Binds on a free port, retrying a lost race: the probe releases the port before the
        /// listener claims it, so a concurrently running test can take it in between.
        /// </summary>
        private static async Task<((IAsyncDisposable Listener, Func<int> AdmittedCount) Started, int Port)> StartOnAFreePortAsync(
            Func<int, X509Certificate2, Task<(IAsyncDisposable Listener, Func<int> AdmittedCount)>> startListener,
            X509Certificate2 certificate)
        {
            for (var attempt = 1; ; attempt++)
            {
                var port = FreePort();
                try
                {
                    return (await startListener(port, certificate), port);
                }
                catch (IOException) when (attempt < 5)
                {
                    // Another listener claimed the probed port; try a different one.
                }
            }
        }

        /// <summary>
        /// Connects a peer the listener is expected to turn away, reporting false when the refusal
        /// arrived as a reset ON the connect rather than after it.
        ///
        /// <para>Refusing ahead of the handshake is <c>ConnectionContext.Abort()</c>, which puts a
        /// RST on the wire without writing a byte. That RST RACES the client's own connect
        /// completion: on loopback the kernel finishes the three-way handshake and queues the
        /// connection the instant <c>connect()</c> is called, so the client is already connected as
        /// far as the wire is concerned — but the completion still has to be dequeued onto a thread
        /// pool thread. When the server's accept-and-abort lands first, the socket's pending error
        /// is reported AS the connect result and <c>ConnectAsync</c> throws. A standalone probe
        /// measured 1885 of 2000 connects to an accept-then-reset loopback listener failing that
        /// way on an IDLE box, so "connect succeeds, then the close is read" is the lucky ordering,
        /// not the normal one; a loaded box merely stops being lucky.</para>
        ///
        /// <para>Both orderings are the SAME server action observed at two different instants, so
        /// the probe classifies the reset as what it is — turned away without a handshake — instead
        /// of retrying the connect until the race lands the other way. That distinction matters:
        /// a retry (or a longer timeout) would leave the fact passing because the race was re-rolled,
        /// whereas this removes the race by covering the whole outcome space of one deterministic
        /// refusal. It cannot mask the defect the fact exists to catch, because a listener that
        /// admits after the handshake never aborts this peer at all — it offers it a handshake, so
        /// the reset branch is unreachable and the handshake assertion still bites.</para>
        ///
        /// <para>Only <see cref="SocketError.ConnectionReset"/> is folded in. <c>ConnectionRefused</c>
        /// (nothing listening at all) and every other socket error still fail the probe.</para>
        /// </summary>
        public static async Task<bool> ConnectAllowingRefusalAtConnectAsync(TcpClient client, int port)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                return false;
            }
        }

        private static async Task WaitForCountAsync(Func<int> count, int expected)
        {
            var deadline = DateTime.UtcNow + Settle;
            while (count() != expected && DateTime.UtcNow < deadline)
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
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                          or InvalidOperationException or SocketException)
            {
                // The socket is already torn down — which is what a refusal at the door looks
                // like once the failed handshake attempt has disposed the client side of it.
                return true;
            }
        }

        /// <summary>
        /// Attempts an implicit-TLS client handshake, reporting completion rather than throwing:
        /// a refusal at the door surfaces as a closed socket or an authentication failure, and the
        /// probe's question is only whether the handshake COMPLETED.
        /// </summary>
        public static async Task<bool> TryImplicitTlsHandshakeAsync(TcpClient client)
        {
            try
            {
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true,
                    userCertificateValidationCallback: (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = "localhost" })
                    .WaitAsync(Settle);
                return true;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException
                                          or OperationCanceledException or TimeoutException
                                          or ObjectDisposedException)
            {
                return false;
            }
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public static X509Certificate2 SelfSigned()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Re-import through PKCS#12 so the private key is usable by SslStream on every platform.
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        }
    }
}

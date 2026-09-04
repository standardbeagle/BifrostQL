using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// End-to-end handshake + authentication tests for the pgwire front door, driven
    /// over a real loopback socket with a hand-written frontend. Proves TLS negotiation,
    /// both auth methods, and — the load-bearing security facts — that a login only
    /// becomes a ready session when it projects to a real Bifrost identity, and is
    /// rejected (never anonymous) otherwise.
    /// </summary>
    public sealed class PgConnectionHandlerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Fact]
        public async Task SslRequest_ThenCleartext_ValidIdentity_ReachesReadyForQuery()
        {
            // Arrange: a TLS-capable front door with a valid tenant login.
            var cert = CreateSelfSignedCert();
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, ServerCertificate = cert });

            // Act: negotiate TLS, authenticate, read to completion.
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.NegotiateTlsAsync();
            await client.SendStartupAsync("alice");
            await client.DoCleartextAsync("s3cret");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert: TLS negotiated + authenticated through to ReadyForQuery.
            result.ReadyForQuery.Should().BeTrue();
            result.WasRejected.Should().BeFalse();
        }

        [Fact]
        public async Task Scram_ValidIdentity_ReachesReadyForQuery()
        {
            // Arrange
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.ScramSha256 });

            // Act
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("alice");
            await client.DoScramAsync("s3cret");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert: SCRAM proved the secret without sending it, session is ready.
            result.ReadyForQuery.Should().BeTrue();
        }

        [Fact]
        public async Task Scram_MalformedClientFirst_IsRejected_WithProtocolViolation()
        {
            // Arrange: a valid user so the server runs the SCRAM exchange (not the decoy path).
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.ScramSha256 });

            // Act: send a malformed client-first (no r= nonce). Before the fix this threw an
            // unhandled PgScramProtocolException to Kestrel and dropped the connection; now it
            // must surface as a graceful protocol_violation ErrorResponse.
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("alice");
            await client.SendMalformedScramFirstAsync();
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert
            result.ReadyForQuery.Should().BeFalse();
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation);
        }

        [Fact]
        public async Task Cleartext_WrongPassword_IsRejected()
        {
            // Arrange
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, AllowCleartextPasswordWithoutTls = true });

            // Act
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("alice");
            await client.DoCleartextAsync("wrong");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidPassword);
        }

        [Fact]
        public async Task Cleartext_WithoutTls_IsRefused_BeforePasswordCheck()
        {
            // With the dev override OFF (the default), a client that skips SSLRequest and
            // tries cleartext auth over the raw socket must be refused with a transport-only
            // protocol_violation — BEFORE any password is read — so a password never crosses
            // the wire in the clear. Non-vacuous: with AllowCleartextPasswordWithoutTls = true
            // this same handshake reaches the password check (see Cleartext_WrongPassword_IsRejected,
            // which returns SqlStateInvalidPassword instead).
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext /* AllowCleartextPasswordWithoutTls defaults to false */ });

            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("alice");
            // No password is sent: the server must reject at the transport gate first.
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            result.ReadyForQuery.Should().BeFalse();
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation,
                "cleartext over a non-TLS connection is refused before the credential is read");
            result.ErrorMessage.Should().Contain("TLS",
                "the refusal names the transport requirement, not the account (no enumeration oracle)");
        }

        [Fact]
        public async Task SubjectLessIdentity_IsRejected_NeverAnonymous()
        {
            // Arrange: the password is correct, but the mapped principal has no subject —
            // authentication succeeds yet the identity must not.
            var store = new FakePgCredentialStore().Add("svc", "key", SubjectLessPrincipal());
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, AllowCleartextPasswordWithoutTls = true });

            // Act
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("svc");
            await client.DoCleartextAsync("key");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert: rejected with invalid_authorization, never a ReadyForQuery session.
            result.ReadyForQuery.Should().BeFalse();
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidAuthorization);
        }

        [Fact]
        public async Task UnmappedOidcIssuer_IsRejected_NeverAnonymous()
        {
            // Arrange: an OIDC claim-mapper registry is present but has no mapper for the
            // token's issuer — reading it locally would silently drop tenant/role claims.
            var services = new ServiceCollection()
                .AddSingleton(new OidcClaimMapperRegistry(
                    Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()))
                .BuildServiceProvider();
            var store = new FakePgCredentialStore().Add("svc", "key", UnmappedIssuerPrincipal());
            await using var fixture = await PgFixture.StartAsync(store, services,
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, AllowCleartextPasswordWithoutTls = true });

            // Act
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("svc");
            await client.DoCleartextAsync("key");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert
            result.ReadyForQuery.Should().BeFalse();
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidAuthorization);
        }

        [Fact]
        public async Task UnknownUser_IsRejected()
        {
            // Arrange: an empty store — no user resolves.
            await using var fixture = await PgFixture.StartAsync(new FakePgCredentialStore(), EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, AllowCleartextPasswordWithoutTls = true });

            // Act
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendStartupAsync("ghost");
            await client.DoCleartextAsync("whatever");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidPassword);
        }

        [Fact]
        public async Task MalformedStartup_IsRejected()
        {
            // Arrange
            await using var fixture = await PgFixture.StartAsync(new FakePgCredentialStore(), EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, AllowCleartextPasswordWithoutTls = true });

            // Act: send an unsupported startup protocol code.
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendBadStartupAsync();
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation);
        }

        [Fact]
        public async Task TlsSession_End_DisposesTheUpgradedStream()
        {
            // LOW: the SslStream from UpgradeToTlsAsync was never disposed (no close_notify,
            // inner socket leaked past the session). The upgraded stream must be disposed when
            // the connection ends. Asserted via a disposal-tracking wrapper around the real
            // SslStream, injected through the handler's TLS-upgrade seam.
            var cert = CreateSelfSignedCert();
            var tracker = new DisposalTrackingStream();
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            var options = new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, ServerCertificate = cert };

            async Task<Stream> TrackTlsUpgrade(Stream inner, CancellationToken ct)
            {
                var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct);
                tracker.Inner = ssl;
                return tracker;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clientSocket = new TcpClient();
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await listener.AcceptTcpClientAsync();
            await connectTask;

            var handler = new PgConnectionHandler(
                store, BifrostAuthContextFactory.Instance, EmptyServices(), options, tlsUpgrade: TrackTlsUpgrade);
            var serverTask = handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None);

            var client = new PgHandshakeClient(clientSocket.GetStream());
            await client.NegotiateTlsAsync();
            await client.SendStartupAsync("alice");
            await client.DoCleartextAsync("s3cret");
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);
            result.ReadyForQuery.Should().BeTrue("the TLS session authenticates normally");

            // End the session; the server must dispose the upgraded stream as it tears down.
            clientSocket.Dispose();
            try { await serverTask.WaitAsync(Timeout); } catch { /* teardown races are fine */ }
            listener.Stop();

            tracker.Disposed.Should().BeTrue(
                "the TLS-upgraded stream must be disposed when the connection ends (close_notify, inner socket release)");
        }

        /// <summary>A delegating stream that records its own disposal.</summary>
        private sealed class DisposalTrackingStream : Stream
        {
            public Stream Inner { get; set; } = Stream.Null;
            public bool Disposed { get; private set; }

            public override bool CanRead => Inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => Inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => Inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => Inner.FlushAsync(ct);
            public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Inner.ReadAsync(buffer, ct);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => Inner.WriteAsync(buffer, ct);

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                if (disposing) Inner.Dispose();
                base.Dispose(disposing);
            }
        }

        [Fact]
        public async Task Scram_PerSourceThrottle_RefusesBeforeChallenge()
        {
            // LOW: one SCRAM attempt per connection costs PBKDF2(4096) with no per-source
            // throttle. With MaxAuthAttemptsPerSource = 2, the third connection from the same
            // source must be refused BEFORE the SASL challenge is issued (no hash work), with
            // the SAME invalid_password wire shape as a wrong password (no throttle oracle).
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            var options = new PgWireOptions { AuthMethod = PgAuthMethod.ScramSha256, MaxAuthAttemptsPerSource = 2 };
            var handler = new PgConnectionHandler(store, BifrostAuthContextFactory.Instance, EmptyServices(), options);
            const string source = "203.0.113.7:5000";

            // Attempts 1-2 (wrong password): admitted to the SCRAM exchange — the challenge
            // is issued and the outcome is the ordinary invalid_password rejection.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (client, cleanup) = await StartConnectionAsync(handler, source);
                await client.SendStartupAsync("alice");
                await client.DoScramExpectingFailureAsync("wrong");
                var rejected = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);
                rejected.WasRejected.Should().BeTrue();
                rejected.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidPassword);
                await cleanup();
            }

            // Attempt 3: over the per-source cap. The FIRST backend message after startup is
            // the ErrorResponse — no AuthenticationSASL challenge is issued, proving the
            // refusal precedes any credential lookup or PBKDF2 work.
            var (throttled, throttledCleanup) = await StartConnectionAsync(handler, source);
            await throttled.SendStartupAsync("alice");
            var first = await throttled.ReadNextMessageAsync().WaitAsync(Timeout);
            PgHandshakeClient.ErrorSqlStateOf(first.Type, first.Body).Should().Be(
                PgWireProtocol.SqlStateInvalidPassword,
                "a rate-limited source is refused with the same wire shape as a failed auth, before the challenge");
            await throttledCleanup();
        }

        /// <summary>Opens a loopback connection, pumps the handler on the server end, and returns the
        /// client plus a cleanup that tears the connection down and drains the server task.</summary>
        private static async Task<(PgHandshakeClient Client, Func<Task> Cleanup)> StartConnectionAsync(
            PgConnectionHandler handler, string source)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clientSocket = new TcpClient();
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await listener.AcceptTcpClientAsync();
            await connectTask;

            var serverTask = handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None, source);
            return (new PgHandshakeClient(clientSocket.GetStream()), async () =>
            {
                clientSocket.Dispose();
                try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* connection teardown races are expected on dispose */ }
                serverSocket.Dispose();
                listener.Stop();
            });
        }

        // ---- fixtures / principals -----------------------------------------
        private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "pgwire"));

        private static ClaimsPrincipal SubjectLessPrincipal() =>
            new(new ClaimsIdentity(new[] { new Claim("scope", "read") }, authenticationType: "pgwire"));

        private static ClaimsPrincipal UnmappedIssuerPrincipal() =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "svc-1"),
                new Claim("iss", "https://evil.example/"),
            }, authenticationType: "pgwire"));

        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            // Re-import from PFX so the private key is usable by SslStream on every OS.
#pragma warning disable SYSLIB0057 // portable across net8/9/10 target frameworks
            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
        }

        /// <summary>Loopback socket pair with the handler pumping the server end.</summary>
        private sealed class PgFixture : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _clientSocket;
            private readonly TcpClient _serverSocket;
            private readonly Task _serverTask;

            public Stream ClientStream { get; }

            private PgFixture(TcpListener listener, TcpClient clientSocket, TcpClient serverSocket, Task serverTask)
            {
                _listener = listener;
                _clientSocket = clientSocket;
                _serverSocket = serverSocket;
                _serverTask = serverTask;
                ClientStream = clientSocket.GetStream();
            }

            public static async Task<PgFixture> StartAsync(
                IPgCredentialStore store, IServiceProvider services, PgWireOptions options)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                var clientSocket = new TcpClient();
                var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
                var serverSocket = await listener.AcceptTcpClientAsync();
                await connectTask;

                var handler = new PgConnectionHandler(store, BifrostAuthContextFactory.Instance, services, options);
                var serverTask = handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None);
                return new PgFixture(listener, clientSocket, serverSocket, serverTask);
            }

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
}

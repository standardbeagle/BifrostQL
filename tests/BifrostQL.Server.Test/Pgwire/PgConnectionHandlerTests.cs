using System.Net;
using System.Net.Sockets;
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
        public async Task Cleartext_WrongPassword_IsRejected()
        {
            // Arrange
            var store = new FakePgCredentialStore().Add("alice", "s3cret", TenantPrincipal("user-alice", "tenant-a"));
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext });

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
        public async Task SubjectLessIdentity_IsRejected_NeverAnonymous()
        {
            // Arrange: the password is correct, but the mapped principal has no subject —
            // authentication succeeds yet the identity must not.
            var store = new FakePgCredentialStore().Add("svc", "key", SubjectLessPrincipal());
            await using var fixture = await PgFixture.StartAsync(store, EmptyServices(),
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext });

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
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext });

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
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext });

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
                new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext });

            // Act: send an unsupported startup protocol code.
            var client = new PgHandshakeClient(fixture.ClientStream);
            await client.SendBadStartupAsync();
            var result = await client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            // Assert
            result.WasRejected.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation);
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

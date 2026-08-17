using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The front door as an operator actually deploys it: <c>AddBifrostLdap</c> on a real Kestrel
    /// host, both listeners bound to real TCP ports, driven by a socket client that speaks BER.
    ///
    /// <para>Everything else in the LDAP suite drives <see cref="LdapConnectionHandler"/> over a
    /// loopback stream, which cannot see the registration itself: whether the ports actually bind,
    /// whether the bind address is the declared posture, whether Kestrel routes each port to the
    /// handler the registration intended, and — the gap this file closes — whether
    /// <see cref="LdapsConnectionHandler"/> runs a real handshake and then the same session loop.
    /// Until now that handler was only ever COMPOSITION-tested: proven to exist in the container,
    /// never proven to serve a byte.</para>
    ///
    /// <para>The two confidential routes are asserted to be equivalent, not merely both present:
    /// implicit TLS on the LDAPS port and a StartTLS upgrade on the cleartext port must reach the
    /// same bind and the same tenant-scoped answer, because they converge on one session loop.</para>
    /// </summary>
    public sealed class LdapListenerEndToEndTests : IAsyncLifetime
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private IHost _host = null!;
        private int _port;
        private int _ldapsPort;

        public async Task InitializeAsync()
        {
            _port = FreePort();
            _ldapsPort = FreePort();

            var model = LdapModelBuilder.Create().WithPeople().WithGroups().Build();
            var pipeline = new LdapFakeIntentExecutor(model)
                .WithPeople(3, tenant: "acme", startId: 1)
                .WithPeople(3, tenant: "globex", startId: 20);

            _host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel()
                    .ConfigureServices(services =>
                    {
                        // The two seams a deployment MUST register; without either, bind is
                        // fail-closed and no ambient credential store stands in.
                        services.AddSingleton<ILdapCredentialStore, LdapTestIdentity.Store>();
                        services.AddSingleton<ILdapPasswordHasher, LdapTestIdentity.Hasher>();
                        services.AddSingleton<IBifrostAuthContextFactory, LdapTestIdentity.Factory>();
                        // The read seam. Registered as the interface, so the registration wires
                        // search exactly as it would against a real BifrostQL engine.
                        services.AddSingleton<IQueryIntentExecutor>(pipeline);

                        services.AddBifrostLdap(o =>
                        {
                            o.Port = _port;
                            o.LdapsPort = _ldapsPort;
                            o.ServerCertificate = LdapTestCertificate.Instance;
                            o.PagedResultsCookieSecret = "listener-test-cookie-secret";
                        });
                    })
                    .Configure(_ => { }))
                .Build();

            await _host.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        /// <summary>
        /// An ephemeral port the OS is not currently using. Kestrel binds these itself, and a
        /// connection handler listener does not publish its assigned port back through the
        /// addresses feature, so the port has to be chosen before the host starts.
        /// </summary>
        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static async Task<(TcpClient Socket, LdapTestClient Client)> ConnectAsync(int port)
        {
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);
            return (socket, new LdapTestClient(socket.GetStream()));
        }

        /// <summary>Connects to the implicit-TLS port and completes the handshake before any LDAP byte.</summary>
        private async Task<(TcpClient Socket, LdapTestClient Client)> ConnectLdapsAsync()
        {
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, _ldapsPort).WaitAsync(Timeout);

            var ssl = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }).WaitAsync(Timeout);

            return (socket, new LdapTestClient(ssl));
        }

        private static async Task<LdapResponse> ReadAsync(LdapTestClient client)
        {
            var response = await client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull("the listener must answer rather than leave the client waiting");
            return response!;
        }

        private static async Task BindAsync(LdapTestClient client, string uid, int messageId = 1)
        {
            await client.SendAsync(LdapWire.Message(messageId, LdapWire.BindRequest(
                name: LdapTestIdentity.Dn(uid), password: LdapTestIdentity.Password)));
            var response = await ReadAsync(client);
            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.Success);
        }

        /// <summary>Sends a whole-subtree search and drains entries up to the terminating Done.</summary>
        private static async Task<(List<string> Dns, LdapResponse Done)> SearchAsync(
            LdapTestClient client, int messageId = 2)
        {
            await client.SendAsync(LdapWire.Message(messageId, LdapWire.SearchRequest(
                "dc=example,dc=com", LdapSearchScope.WholeSubtree)));

            var dns = new List<string>();
            while (true)
            {
                var response = await ReadAsync(client);
                if (response.OpTag == LdapProtocol.SearchResultDone)
                    return (dns, response);
                response.OpTag.Should().Be(LdapProtocol.SearchResultEntry);
                dns.Add(response.ObjectName!);
            }
        }

        [Fact]
        public async Task Ldaps_HandshakesOnItsOwnPort_ThenBindsAndSearchesThroughTheSameSessionLoop()
        {
            var (socket, client) = await ConnectLdapsAsync();
            using (socket)
            {
                await BindAsync(client, "alice");

                var (dns, done) = await SearchAsync(client);

                done.ResultCode.Should().Be(LdapResultCode.Success);
                dns.Should().BeEquivalentTo(new[]
                {
                    "uid=user0001,ou=people,dc=example,dc=com",
                    "uid=user0002,ou=people,dc=example,dc=com",
                    "uid=user0003,ou=people,dc=example,dc=com",
                });
                dns.Should().NotContain(dn => dn.Contains("user002", StringComparison.Ordinal),
                    "the LDAPS route is scoped by the bound identity like any other");
            }
        }

        [Fact]
        public async Task Ldaps_ARawCleartextClientOnTheTlsPort_GetsNoLdapResponse()
        {
            // The implicit-TLS port never speaks LDAP in the clear. A client that skips the
            // handshake and sends BER straight down the socket must not be served: a failed
            // handshake closes silently, because nothing sayable here would be readable.
            var (socket, client) = await ConnectAsync(_ldapsPort);
            using (socket)
            {
                await client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(
                    name: LdapTestIdentity.Dn("alice"), password: LdapTestIdentity.Password)));

                var act = async () => await client.ReadResponseAsync().WaitAsync(Timeout);

                // Either a clean EOF or a reset — never a BindResponse.
                try
                {
                    (await act()).Should().BeNull("the LDAPS port answers nothing to a cleartext peer");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
                {
                    // The peer's bytes were read as a TLS record and rejected; the socket dropped.
                }
            }
        }

        [Fact]
        public async Task CleartextPort_RefusesACredentialedBind_UntilStartTlsCompletes()
        {
            var (socket, client) = await ConnectAsync(_port);
            using (socket)
            {
                // Before any upgrade: the credential is refused on transport grounds alone, and
                // the connection stays open so a correct client can upgrade and retry.
                await client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(
                    name: LdapTestIdentity.Dn("alice"), password: LdapTestIdentity.Password)));
                var refused = await ReadAsync(client);
                refused.OpTag.Should().Be(LdapProtocol.BindResponse);
                refused.ResultCode.Should().Be(LdapResultCode.ConfidentialityRequired);

                // StartTLS on the SAME connection.
                await client.SendAsync(LdapWire.Message(2, LdapWire.ExtendedRequest(LdapProtocol.StartTlsOid)));
                var upgrade = await ReadAsync(client);
                upgrade.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
                upgrade.ResultCode.Should().Be(LdapResultCode.Success);
                await client.UpgradeToTlsAsync();

                // Behind the upgrade the same credential is accepted, and the same data answers.
                await BindAsync(client, "alice", messageId: 3);
                var (dns, done) = await SearchAsync(client, messageId: 4);

                done.ResultCode.Should().Be(LdapResultCode.Success);
                dns.Should().HaveCount(3,
                    "StartTLS and LDAPS converge on one session loop, so they serve identically");
            }
        }

        [Fact]
        public async Task BothConfidentialRoutes_ScopeTheSameRequestToTheIdentityThatBound()
        {
            // One request text, two transports, two identities: the answers must be disjoint and
            // must not depend on which confidential route the client took.
            var (ldapsSocket, ldapsClient) = await ConnectLdapsAsync();
            using (ldapsSocket)
            {
                await BindAsync(ldapsClient, "bob"); // tenant globex over implicit TLS
                var (globexDns, globexDone) = await SearchAsync(ldapsClient);

                var (startTlsSocket, startTlsClient) = await ConnectAsync(_port);
                using (startTlsSocket)
                {
                    await startTlsClient.SendAsync(
                        LdapWire.Message(1, LdapWire.ExtendedRequest(LdapProtocol.StartTlsOid)));
                    (await ReadAsync(startTlsClient)).ResultCode.Should().Be(LdapResultCode.Success);
                    await startTlsClient.UpgradeToTlsAsync();

                    await BindAsync(startTlsClient, "alice", messageId: 2); // tenant acme over StartTLS
                    var (acmeDns, acmeDone) = await SearchAsync(startTlsClient, messageId: 3);

                    globexDone.ResultCode.Should().Be(LdapResultCode.Success);
                    acmeDone.ResultCode.Should().Be(LdapResultCode.Success);
                    globexDns.Should().NotBeEmpty();
                    acmeDns.Should().NotBeEmpty();
                    globexDns.Should().NotIntersectWith(acmeDns);
                }
            }
        }

        [Fact]
        public async Task BothPorts_ActuallyBind_AndRouteToTheirOwnHandler()
        {
            // What an end-to-end test can decide about the posture: that BOTH declared ports came
            // up on loopback and each routes to the handler its registration named — the cleartext
            // port to the session loop (which answers StartTLS) and the TLS port to the LDAPS
            // handler (which answers nothing until a handshake completes).
            //
            // The complementary half — that neither port is ALSO on a wildcard address — is
            // deliberately NOT asserted here. Deciding it requires a local non-loopback interface,
            // and connecting to an address nothing is bound to fails under either posture, so on a
            // loopback-only machine (a container, this WSL host) the check passes without
            // discriminating anything: a guard that reads as coverage and proves nothing. The
            // decidable statement of that fact lives in LdapListenerPostureTests, which pins
            // BindAddress at the registration where the choice is actually made.
            foreach (var port in new[] { _port, _ldapsPort })
            {
                using var probe = new TcpClient();
                var act = async () => await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);
                await act.Should().NotThrowAsync($"the front door declares port {port} and must serve it");
            }

            // The cleartext port speaks LDAP immediately...
            var (cleartextSocket, cleartextClient) = await ConnectAsync(_port);
            using (cleartextSocket)
            {
                await cleartextClient.SendAsync(
                    LdapWire.Message(1, LdapWire.ExtendedRequest(LdapProtocol.StartTlsOid)));
                var upgrade = await ReadAsync(cleartextClient);
                upgrade.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
                upgrade.ResultCode.Should().Be(LdapResultCode.Success,
                    "the cleartext port routes to the session loop, which offers StartTLS");
            }

            // ...while the TLS port produced a working LDAP session only after a handshake, which
            // Ldaps_HandshakesOnItsOwnPort_ThenBindsAndSearchesThroughTheSameSessionLoop proves.
        }
    }
}

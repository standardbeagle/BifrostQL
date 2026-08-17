using System.Security.Claims;
using System.Text;
using BifrostQL.Server;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The StartTLS state machine (RFC 4511 §4.14 / RFC 4513 §3.1). Pins that exactly one session
    /// state negotiates — pre-bind, not yet confidential, certificate configured — and that every
    /// other state is refused with the session left as it was, so no ordering of requests reaches a
    /// mixed or downgraded transport.
    /// </summary>
    public sealed class LdapStartTlsTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private sealed class Store : ILdapCredentialStore
        {
            public readonly List<string> Lookups = new();

            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                Lookups.Add(bindDn);
                if (!string.Equals(bindDn, "uid=alice", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<LdapCredentialRecord?>(null);
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

        /// <summary>Options with a usable certificate: StartTLS is available on this listener.</summary>
        private static LdapWireOptions WithCertificate() =>
            new() { ServerCertificate = LdapTestCertificate.Instance };

        private static LdapBindAuthenticator Authenticator(Store store, LdapWireOptions options) =>
            new(store, new Hasher(), new Factory(), options);

        private static async Task<LdapResponse> ReadAsync(LdapFixture fixture)
        {
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull("the server must answer this request");
            return response!;
        }

        private static byte[] StartTls(int messageId) =>
            LdapWire.Message(messageId, LdapWire.ExtendedRequest(LdapProtocol.StartTlsOid));

        [Fact]
        public async Task StartTls_Upgrades_AndTheCredentialGateOpensBehindIt()
        {
            var options = WithCertificate();
            var store = new Store();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            // Before the upgrade the credential is refused without a lookup.
            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.ConfidentialityRequired);
            store.Lookups.Should().BeEmpty();

            await fixture.Client.SendAsync(StartTls(2));
            var startTls = await ReadAsync(fixture);
            startTls.MessageId.Should().Be(2);
            startTls.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            startTls.ResultCode.Should().Be(LdapResultCode.Success);

            await fixture.Client.UpgradeToTlsAsync();

            // The same credential now travels a confidential transport and is verified.
            await fixture.Client.SendAsync(LdapWire.Message(3, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            var bind = await ReadAsync(fixture);
            bind.MessageId.Should().Be(3);
            bind.ResultCode.Should().Be(LdapResultCode.Success);
            store.Lookups.Should().ContainSingle();
        }

        [Fact]
        public async Task StartTls_WithNoCertificateConfigured_IsUnavailable_AndTheSessionStaysCleartext()
        {
            var options = new LdapWireOptions();
            var store = new Store();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            await fixture.Client.SendAsync(StartTls(1));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            response.ResultCode.Should().Be(LdapResultCode.Unavailable);

            // It did not half-upgrade: the connection is still cleartext, so credentials stay refused.
            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.ConfidentialityRequired);
            store.Lookups.Should().BeEmpty();
        }

        [Fact]
        public async Task StartTls_ASecondTime_IsRefused_AndTheEstablishedTlsKeepsWorking()
        {
            var options = WithCertificate();
            var store = new Store();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            await fixture.Client.SendAsync(StartTls(1));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);
            await fixture.Client.UpgradeToTlsAsync();

            // A second StartTLS, now inside the TLS session.
            await fixture.Client.SendAsync(StartTls(2));
            var second = await ReadAsync(fixture);
            second.MessageId.Should().Be(2);
            second.ResultCode.Should().Be(LdapResultCode.OperationsError,
                "renegotiating would tear down a working confidential stream on request");

            // The existing TLS session is untouched and still usable.
            await fixture.Client.SendAsync(LdapWire.Message(3, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);
        }

        [Fact]
        public async Task StartTls_OnAnLdapsConnection_IsRefused()
        {
            var options = WithCertificate();
            var store = new Store();
            await using var fixture = await LdapFixture.StartAsync(
                options, authenticator: Authenticator(store, options), tls: true);

            await fixture.Client.SendAsync(StartTls(1));
            var response = await ReadAsync(fixture);

            response.ResultCode.Should().Be(LdapResultCode.OperationsError,
                "an implicit-TLS connection is already confidential");
        }

        [Fact]
        public async Task StartTls_AfterASuccessfulBind_IsRefused()
        {
            // An anonymous bind authenticates the session on a cleartext connection (it carries no
            // secret), so this reaches the already-bound branch rather than the already-TLS one.
            var options = WithCertificate();
            options.AnonymousBindEnabled = true;
            var store = new Store();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "", password: "")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            await fixture.Client.SendAsync(StartTls(2));
            var response = await ReadAsync(fixture);

            response.ResultCode.Should().Be(LdapResultCode.OperationsError,
                "installing TLS under an existing association is a state-confusion hazard");
        }

        [Fact]
        public async Task PlaintextPipelinedBehindStartTls_IsFatal_AndNoTlsIsNegotiated()
        {
            var options = WithCertificate();
            var store = new Store();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            // ONE write carrying StartTLS immediately followed by a search: bytes written in the clear
            // on the assumption they would be processed after the upgrade.
            var pipelined = StartTls(1).Concat(LdapWire.Message(2, LdapWire.SearchRequest())).ToArray();
            await fixture.Client.SendAsync(pipelined);

            var response = await ReadAsync(fixture);
            response.MessageId.Should().Be(0, "the Notice of Disconnection is sent on message ID 0");
            response.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            response.ResultCode.Should().Be(LdapResultCode.ProtocolError);

            // No StartTLS success was sent and no handshake started: the connection just closes.
            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("pipelined plaintext closes the connection");
        }

        [Fact]
        public async Task OversizedFrameBeforeStartTls_IsAProtocolError_AndClosesCleanly()
        {
            var options = WithCertificate();
            options.MaxMessageLength = 4096;
            await using var fixture = await LdapFixture.StartAsync(options);

            // An envelope declaring 16 MiB of body — refused on the length prefix, before any
            // allocation, and before any transport upgrade could be attempted.
            await fixture.Client.SendAsync(new byte[] { LdapProtocol.Sequence, 0x84, 0x01, 0x00, 0x00, 0x00 });

            var response = await ReadAsync(fixture);
            response.ResultCode.Should().Be(LdapResultCode.ProtocolError);
            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout)).Should().BeNull();
        }

        [Fact]
        public async Task CancellationDuringTheHandshake_EndsTheConnectionWithoutThrowing()
        {
            var options = WithCertificate();
            await using var fixture = await LdapFixture.StartAsync(options);

            await fixture.Client.SendAsync(StartTls(1));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            // The client never sends its ClientHello; the server is blocked in the handshake when the
            // host shuts the connection token down.
            await fixture.Cancellation.CancelAsync();

            await fixture.ServerTask.WaitAsync(Timeout);
            fixture.ServerTask.IsCompletedSuccessfully.Should().BeTrue(
                "a cancelled handshake ends the connection cleanly, never as an unhandled fault");
        }

        [Fact]
        public async Task UnbindOverTls_ClosesCleanly_WithNoResponse()
        {
            var options = WithCertificate();
            await using var fixture = await LdapFixture.StartAsync(options, tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.UnbindRequest()));

            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("Unbind closes the connection with no reply");
            await fixture.ServerTask.WaitAsync(Timeout);
            fixture.ServerTask.IsCompletedSuccessfully.Should().BeTrue();
        }
    }
}

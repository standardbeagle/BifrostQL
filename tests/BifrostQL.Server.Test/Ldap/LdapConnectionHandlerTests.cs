using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// End-to-end connection-loop tests for the LDAP front door, driven over a real loopback socket
    /// with hand-built BER. Proves the lifecycle facts of the slice: every understood-but-not-yet-
    /// executed op (Bind, Search, ExtendedRequest) is answered with <c>unwillingToPerform</c> and the
    /// connection stays usable; Unbind closes; Abandon is a silent no-op that does not desync the
    /// loop; malformed BER draws a Notice of Disconnection then a clean close (never a hang, never a
    /// process exception); and idle / connection limits are enforced.
    /// </summary>
    public sealed class LdapConnectionHandlerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static async Task<LdapResponse> ReadAsync(LdapFixture fixture)
        {
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull("the server must answer this request");
            return response!;
        }

        [Fact]
        public async Task BindRequest_IsAnswered_UnwillingToPerform_ConnectionStaysOpen()
        {
            await using var fixture = await LdapFixture.StartAsync();

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "cn=admin", password: "x")));
            var response = await ReadAsync(fixture);

            response.MessageId.Should().Be(1);
            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.UnwillingToPerform);

            // The connection is still usable: a second request is answered on the same socket.
            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest()));
            (await ReadAsync(fixture)).MessageId.Should().Be(2);
        }

        [Fact]
        public async Task SearchRequest_IsAnswered_SearchResultDone_UnwillingToPerform()
        {
            await using var fixture = await LdapFixture.StartAsync();

            await fixture.Client.SendAsync(LdapWire.Message(3, LdapWire.SearchRequest(baseObject: "dc=example,dc=com")));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.SearchResultDone);
            response.ResultCode.Should().Be(LdapResultCode.UnwillingToPerform);
        }

        [Fact]
        public async Task ExtendedRequest_StartTls_IsAnswered_ExtendedResponse_UnwillingToPerform()
        {
            await using var fixture = await LdapFixture.StartAsync();

            await fixture.Client.SendAsync(LdapWire.Message(4, LdapWire.ExtendedRequest(LdapProtocol.StartTlsOid)));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            response.ResultCode.Should().Be(LdapResultCode.UnwillingToPerform);
        }

        [Fact]
        public async Task UnbindRequest_ClosesConnection_WithNoResponse()
        {
            await using var fixture = await LdapFixture.StartAsync();

            await fixture.Client.SendAsync(LdapWire.Message(5, LdapWire.UnbindRequest()));

            // Unbind has no response; the connection closes (the client observes EOF, not a hang).
            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("Unbind closes the connection with no reply");
        }

        [Fact]
        public async Task AbandonRequest_IsSilentNoOp_LoopStaysInSync()
        {
            await using var fixture = await LdapFixture.StartAsync();

            // Abandon produces no reply; the following Bind must be the first (and only) response —
            // proving Abandon neither replied nor desynced the message loop.
            await fixture.Client.SendAsync(LdapWire.Message(6, LdapWire.AbandonRequest(1)));
            await fixture.Client.SendAsync(LdapWire.Message(7, LdapWire.BindRequest()));

            var response = await ReadAsync(fixture);
            response.MessageId.Should().Be(7);
            response.OpTag.Should().Be(LdapProtocol.BindResponse);
        }

        [Fact]
        public async Task MalformedBer_DrawsNoticeOfDisconnection_ThenCloses()
        {
            await using var fixture = await LdapFixture.StartAsync();

            // A well-framed envelope (messageID=1) whose protocolOp element lies about its length:
            // op tag 0x63 declares 0x7F content bytes but none remain — a decode-time BER violation.
            var malformed = new byte[] { LdapProtocol.Sequence, 0x05, 0x02, 0x01, 0x01, 0x63, 0x7F };
            await fixture.Client.SendAsync(malformed);

            var response = await ReadAsync(fixture);
            response.MessageId.Should().Be(0, "the Notice of Disconnection is sent on message ID 0");
            response.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            response.ResultCode.Should().Be(LdapResultCode.ProtocolError);

            // Then the connection closes — no hang, no process exception.
            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("a fatal protocol error closes the connection after the notice");
        }

        [Fact]
        public async Task IdleConnection_IsClosed_AfterIdleTimeout()
        {
            await using var fixture = await LdapFixture.StartAsync(
                new LdapWireOptions { IdleTimeout = TimeSpan.FromMilliseconds(200) });

            // Send nothing: the server must close the idle connection, not hold it open forever.
            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("an idle connection is closed after the idle timeout");
        }

        [Fact]
        public async Task Connection_OverTheConnectionCap_IsRefused()
        {
            // Arrange: a shared limiter already at its ceiling of 1 (one slot pre-taken elsewhere).
            var limiter = new LdapBoundedCounter(1, "MaxConnections");
            limiter.TryAcquire().Should().BeTrue();

            await using var fixture = await LdapFixture.StartAsync(connectionLimiter: limiter);

            // The refused connection draws a Notice of Disconnection then closes immediately.
            var response = await ReadAsync(fixture);
            response.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            (await fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("a connection refused at the cap is closed after the notice");
        }
    }
}

using System.Text;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Unit tests for the SCRAM-SHA-256 server exchange, driven by an independent test
    /// client (<see cref="ScramTestClient"/>) so both sides of RFC 7677 are exercised.
    /// </summary>
    public sealed class ScramSha256ServerTests
    {
        [Fact]
        public void CorrectPassword_ProducesVerifiableServerSignature()
        {
            // Arrange: fixed salt/iterations for determinism; client and server share the secret.
            const string password = "s3cr3t-api-key";
            var server = new ScramSha256Server(PgScramVerifier.Derive(password, Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));
            var client = new ScramTestClient();

            // Act: full round trip.
            var serverFirst = server.HandleClientFirst(client.ClientFirstMessage());
            var clientFinal = client.ClientFinalMessage(serverFirst, password, out var expectedServerSignature);
            var serverFinal = server.HandleClientFinal(clientFinal);

            // Assert: the server proved possession of the shared secret to the client.
            serverFinal.Should().Be($"v={expectedServerSignature}");
        }

        [Fact]
        public void WrongPassword_FailsProofVerification()
        {
            // Arrange: the client computes its proof with a different secret than the server holds.
            var server = new ScramSha256Server(PgScramVerifier.Derive("correct-horse", Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));
            var client = new ScramTestClient();
            var serverFirst = server.HandleClientFirst(client.ClientFirstMessage());
            var clientFinal = client.ClientFinalMessage(serverFirst, "battery-staple", out _);

            // Act + Assert: the mismatch surfaces as an authentication failure, fail closed.
            var act = () => server.HandleClientFinal(clientFinal);
            act.Should().Throw<PgScramAuthenticationException>();
        }

        [Fact]
        public void MalformedClientFirst_MissingNonce_Throws()
        {
            // Arrange
            var server = new ScramSha256Server(PgScramVerifier.Derive("pw", Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));

            // Act + Assert: a GS2 header with no r= field is a protocol violation.
            var act = () => server.HandleClientFirst("n,,n=user");
            act.Should().Throw<PgScramProtocolException>();
        }

        [Theory]
        [InlineData("p=tls-server-end-point,,")] // channel binding the server never advertised (no -PLUS)
        [InlineData("n,a=admin,")]               // an authorization identity, which pgwire does not support
        [InlineData("x,,")]                      // not a GS2 channel-binding flag at all
        public void ClientFirst_WithUnsupportedGs2Header_IsAProtocolViolation(string gs2Header)
        {
            var server = new ScramSha256Server(PgScramVerifier.Derive("pw", Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));
            var client = new ScramTestClient(gs2Header);

            var act = () => server.HandleClientFirst(client.ClientFirstMessage());

            act.Should().Throw<PgScramProtocolException>();
        }

        [Fact]
        public void ClientFinal_WhoseChannelBindingDoesNotEchoTheGs2Header_IsRefused_EvenWithAValidProof()
        {
            // The client-first said "n,," but the client-final claims c=eSws (base64 "y,,"). The proof
            // is computed over the client's own message, so it verifies; only the c= check catches
            // that the two rounds disagree about channel binding (RFC 5802 section 7).
            const string password = "pw";
            var server = new ScramSha256Server(PgScramVerifier.Derive(password, Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));
            var client = new ScramTestClient("n,,", channelBinding: "eSws");
            var serverFirst = server.HandleClientFirst(client.ClientFirstMessage());
            var clientFinal = client.ClientFinalMessage(serverFirst, password, out _);

            var act = () => server.HandleClientFinal(clientFinal);

            act.Should().Throw<PgScramProtocolException>();
        }

        [Fact]
        public void ClientFirst_WithYFlag_AndMatchingChannelBinding_Authenticates()
        {
            // Control: "y,," (client supports binding, believes the server does not) is legal while
            // the server advertises no -PLUS mechanism, and c= must echo it.
            const string password = "pw";
            var server = new ScramSha256Server(PgScramVerifier.Derive(password, Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));
            var client = new ScramTestClient("y,,");
            var serverFirst = server.HandleClientFirst(client.ClientFirstMessage());
            var clientFinal = client.ClientFinalMessage(serverFirst, password, out var expected);

            server.HandleClientFinal(clientFinal).Should().Be($"v={expected}");
        }

        [Fact]
        public void ReplayedNonceMismatch_Throws()
        {
            // Arrange: build a valid final message, then corrupt the echoed nonce.
            const string password = "pw";
            var server = new ScramSha256Server(PgScramVerifier.Derive(password, Encoding.ASCII.GetBytes("0123456789abcdef"), 4096));
            var client = new ScramTestClient();
            var serverFirst = server.HandleClientFirst(client.ClientFirstMessage());
            var clientFinal = client.ClientFinalMessage(serverFirst, password, out _);
            var tampered = clientFinal.Replace("c=biws,r=", "c=biws,r=Z", StringComparison.Ordinal);

            // Act + Assert
            var act = () => server.HandleClientFinal(tampered);
            act.Should().Throw<PgScramProtocolException>();
        }
    }
}

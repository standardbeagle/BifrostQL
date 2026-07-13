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
            var server = new ScramSha256Server(password, Encoding.ASCII.GetBytes("0123456789abcdef"), 4096);
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
            var server = new ScramSha256Server("correct-horse", Encoding.ASCII.GetBytes("0123456789abcdef"), 4096);
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
            var server = new ScramSha256Server("pw", Encoding.ASCII.GetBytes("0123456789abcdef"), 4096);

            // Act + Assert: a GS2 header with no r= field is a protocol violation.
            var act = () => server.HandleClientFirst("n,,n=user");
            act.Should().Throw<PgScramProtocolException>();
        }

        [Fact]
        public void ReplayedNonceMismatch_Throws()
        {
            // Arrange: build a valid final message, then corrupt the echoed nonce.
            const string password = "pw";
            var server = new ScramSha256Server(password, Encoding.ASCII.GetBytes("0123456789abcdef"), 4096);
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

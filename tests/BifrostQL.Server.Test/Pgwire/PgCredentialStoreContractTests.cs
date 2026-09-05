using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Contract facts for the pgwire credential store: the store must hold only a SCRAM
    /// verifier (salt, iterations, StoredKey, ServerKey per RFC 5802 §3), never the
    /// plaintext secret, so a leaked store is not wire-usable.
    /// </summary>
    public sealed class PgCredentialStoreContractTests
    {
        [Fact]
        public void Login_contract_carries_no_plaintext_secret()
        {
            // Arrange + Act
            var propertyNames = typeof(PgLogin).GetProperties().Select(p => p.Name).ToList();

            // Assert: the record carries a SCRAM verifier, not the shared secret itself.
            propertyNames.Should().NotContain("Secret",
                "the store must hold a SCRAM verifier, not a plaintext-equivalent secret");
            propertyNames.Should().Contain("Verifier");
        }
    }
}

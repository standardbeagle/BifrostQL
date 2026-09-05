using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Contract facts for the RESP credential store: the store must hold only a one-way
    /// password hash, never a plaintext(-equivalent) secret, so a leaked store does not
    /// hand an attacker wire-usable credentials.
    /// </summary>
    public sealed class RespCredentialStoreContractTests
    {
        [Fact]
        public void Login_contract_carries_no_plaintext_secret()
        {
            // Arrange + Act
            var propertyNames = typeof(RespLogin).GetProperties().Select(p => p.Name).ToList();

            // Assert: the record carries a one-way hash, not the shared secret itself.
            propertyNames.Should().NotContain("Secret",
                "the store must hold a PasswordHasher hash, not a plaintext-equivalent secret");
            propertyNames.Should().Contain("PasswordHash");
        }
    }
}

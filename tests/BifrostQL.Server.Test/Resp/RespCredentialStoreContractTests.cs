using System.Security.Claims;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
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
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

        [Fact]
        public async Task Hash_only_store_authenticates_the_correct_password()
        {
            // Arrange: the store was provisioned with ONLY a PasswordHasher hash of the
            // password (FakeRespCredentialStore.Add hashes at add time) — no plaintext exists.
            var store = new FakeRespCredentialStore().Add("alice", "s3cret", Principal("user-alice"));
            await using var fixture = await RespFixture.StartAsync(
                store, RespFixture.EmptyServices(),
                new RespWireOptions { AllowCleartextAuth = true });

            // Act
            await fixture.Client.SendCommandAsync("AUTH", "alice", "s3cret");
            var reply = await fixture.Client.ReadReplyAsync().WaitAsync(Timeout);

            // Assert
            reply.Should().BeOfType<RespSimpleString>(
                "a verifier-only store must still authenticate the correct password");
        }

        [Fact]
        public async Task Wrong_password_against_hash_only_store_is_refused()
        {
            // Arrange
            var store = new FakeRespCredentialStore().Add("alice", "s3cret", Principal("user-alice"));
            await using var fixture = await RespFixture.StartAsync(
                store, RespFixture.EmptyServices(),
                new RespWireOptions { AllowCleartextAuth = true });

            // Act
            await fixture.Client.SendCommandAsync("AUTH", "alice", "wr0ng");
            var reply = await fixture.Client.ReadReplyAsync().WaitAsync(Timeout);

            // Assert
            reply.Should().BeOfType<RespError>();
        }

        [Fact]
        public async Task Unknown_user_still_pays_one_hash_verification()
        {
            // Arrange: a counting hasher proves the verify runs UNCONDITIONALLY — gating it on
            // user existence (the mutant) makes the unknown-user path run zero verifications
            // and turns the hashing cost into a user-existence timing oracle (invariant 2).
            var hasher = new CountingPasswordHasher();
            var store = new FakeRespCredentialStore().Add("alice", "s3cret", Principal("user-alice"));
            await using var fixture = await RespFixture.StartAsync(
                store, RespFixture.EmptyServices(),
                new RespWireOptions { AllowCleartextAuth = true },
                clock: null, passwordHasher: hasher);

            // Act: one AUTH for an unknown user, one for a wrong password on a known user.
            await fixture.Client.SendCommandAsync("AUTH", "mallory", "whatever");
            (await fixture.Client.ReadReplyAsync().WaitAsync(Timeout)).Should().BeOfType<RespError>();
            await fixture.Client.SendCommandAsync("AUTH", "alice", "wr0ng");
            (await fixture.Client.ReadReplyAsync().WaitAsync(Timeout)).Should().BeOfType<RespError>();

            // Assert: both attempts paid exactly one verification each.
            hasher.VerifyCount.Should().Be(2,
                "an unknown user must spend the same PBKDF2 work as a wrong password");
        }

        private static ClaimsPrincipal Principal(string userId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, "tenant-a"),
            }, authenticationType: "resp"));

        /// <summary>Delegates to the real hasher and counts verifications.</summary>
        private sealed class CountingPasswordHasher : IPasswordHasher<string>
        {
            private readonly PasswordHasher<string> _inner = new();

            public int VerifyCount { get; private set; }

            public string HashPassword(string user, string password) => _inner.HashPassword(user, password);

            public PasswordVerificationResult VerifyHashedPassword(
                string user, string hashedPassword, string providedPassword)
            {
                VerifyCount++;
                return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            }
        }
    }
}


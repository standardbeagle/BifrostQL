using System.Linq;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Criterion 1's structural half: a plaintext comparison and a DEFAULT AMBIENT credential store
    /// must be impossible, not merely absent by convention. The behavioural half (a registered hasher
    /// verifies a hash, never a plaintext) is pinned by <see cref="LdapBindAuthenticatorTests"/>;
    /// these assertions pin the COMPOSITION: the shipped assembly contains no implementation of
    /// either credential seam, registration adds no default for either, and a listener composed
    /// without both seams cannot authenticate anybody — it refuses every bind.
    /// </summary>
    public sealed class LdapCredentialCompositionTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Fact]
        public void ServerAssembly_ShipsNoImplementation_OfEitherCredentialSeam()
        {
            // The only way to authenticate a bind is a hasher a DEPLOYMENT registers. If this assembly
            // ever shipped an ILdapPasswordHasher (a plaintext comparer is the obvious one) or an
            // ILdapCredentialStore, `AddBifrostLdap` could wire it as an ambient default and the
            // fail-closed property would be one TryAddSingleton away from gone.
            var shipped = typeof(BifrostLdapExtensions).Assembly.GetTypes()
                .Where(t => t is { IsInterface: false, IsAbstract: false })
                .Where(t => typeof(ILdapPasswordHasher).IsAssignableFrom(t)
                         || typeof(ILdapCredentialStore).IsAssignableFrom(t))
                .Select(t => t.FullName)
                .ToArray();

            shipped.Should().BeEmpty(
                "BifrostQL ships no credential store and no password hasher — both seams are the deployment's");
        }

        [Fact]
        public void AddBifrostLdap_RegistersNoDefault_StoreOrHasher()
        {
            var services = new ServiceCollection();
            services.AddBifrostLdap(o => o.Port = 3899);

            services.Should().NotContain(d => d.ServiceType == typeof(ILdapCredentialStore),
                "there is no default ambient credential store (criterion 1, fail-closed)");
            services.Should().NotContain(d => d.ServiceType == typeof(ILdapPasswordHasher),
                "there is no default password hasher, so no bind can be verified without an explicit one");
        }

        [Fact]
        public async Task ListenerComposed_WithoutBothSeams_RefusesEveryBind()
        {
            // The DI-composed handler — the one a real host gets — is fail-closed for authentication:
            // absent either seam it is built with no authenticator and refuses binds outright, rather
            // than falling back to some ambient identity.
            var services = new ServiceCollection();
            services.AddBifrostLdap(o => o.Port = 3899);
            await using var provider = services.BuildServiceProvider();
            var handler = provider.GetRequiredService<LdapConnectionHandler>();

            // Confidential transport, so the refusal proved here is the MISSING-SEAM one and not the
            // transport gate that would otherwise answer first.
            await using var fixture = await LdapFixture.StartAsync(handler: handler, tls: true);
            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));

            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull();
            response!.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.UnwillingToPerform,
                "a listener with no credential seams authenticates nobody");
        }
    }
}

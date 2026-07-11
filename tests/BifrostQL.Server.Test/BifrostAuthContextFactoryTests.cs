using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Covers the shared <see cref="IBifrostAuthContextFactory"/> that every transport
    /// gate (HTTP, binary WebSocket, protocol frontend, workflow endpoints) uses to build
    /// the user context. The three security-relevant paths must hold at the factory level
    /// so no gate can drift: authenticated → full claim projection, unauthenticated →
    /// empty context, unmapped OIDC issuer → fail closed by throwing.
    /// </summary>
    public sealed class BifrostAuthContextFactoryTests
    {
        private static readonly BifrostAuthContextFactory Factory = BifrostAuthContextFactory.Instance;

        [Fact]
        public void CreateUserContext_AuthenticatedPrincipal_ProjectsIdentityIntoBifrostContext()
        {
            // Arrange: a local-auth principal (no issuer claim → local claim path).
            var context = new DefaultHttpContext { User = AuthenticatedLocalPrincipal() };

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: full BifrostContext projection — raw principal preserved plus
            // the legacy per-claim-type arrays.
            userContext.Should().BeOfType<BifrostContext>();
            userContext["user"].Should().BeSameAs(context.User);
            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
        }

        [Fact]
        public void CreateUserContext_UnauthenticatedRequest_YieldsEmptyMutableContext()
        {
            // Arrange: anonymous principal (no authentication type → IsAuthenticated false).
            var context = new DefaultHttpContext();

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: empty and NOT a BifrostContext — no identity keys may appear.
            userContext.Should().NotBeOfType<BifrostContext>();
            userContext.Should().BeEmpty();
            // Downstream (correlation id, profile key) writes into it; it must be mutable.
            userContext["probe"] = 1;
            userContext.Should().ContainKey("probe");
        }

        [Fact]
        public void CreateUserContext_UnmappedOidcIssuer_ThrowsFailClosed()
        {
            // Arrange: an authenticated principal carrying an issuer no mapper is
            // registered for. Reading it through the local claim path would strip its
            // tenant/role claims, so the factory must throw instead.
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim("iss", "https://idp.example.test"),
                }, authenticationType: "oidc")),
                RequestServices = new ServiceCollection()
                    .AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()))
                    .BuildServiceProvider(),
            };

            // Act
            var act = () => Factory.CreateUserContext(context);

            // Assert
            act.Should().Throw<UnmappedOidcIssuerException>()
                .WithMessage("*https://idp.example.test*");
        }

        [Fact]
        public void CreateUserContext_MergeOverload_Authenticated_IdentityKeysWin()
        {
            // Arrange
            var context = new DefaultHttpContext { User = AuthenticatedLocalPrincipal() };
            var existing = new Dictionary<string, object?>
            {
                ["user"] = "spoofed-principal", // must NOT shadow the identity key
                ["frontend-extra"] = 42,        // must be merged
            };

            // Act
            var userContext = Factory.CreateUserContext(context, existing);

            // Assert
            userContext.Should().BeOfType<BifrostContext>();
            userContext["user"].Should().BeSameAs(context.User);
            userContext["frontend-extra"].Should().Be(42);
        }

        [Fact]
        public void CreateUserContext_MergeOverload_Unauthenticated_ReturnsExisting()
        {
            // Arrange
            var context = new DefaultHttpContext();
            var existing = new Dictionary<string, object?> { ["frontend-extra"] = 42 };

            // Act
            var userContext = Factory.CreateUserContext(context, existing);

            // Assert: the frontend-parsed context passes through untouched; an empty
            // one degrades to a fresh empty dictionary.
            userContext.Should().BeSameAs(existing);
            Factory.CreateUserContext(context, new Dictionary<string, object?>())
                .Should().BeEmpty();
        }

        [Fact]
        public void Resolve_PrefersDiRegisteredFactory_FallsBackToSharedDefault()
        {
            // Arrange: a host that registered its own factory.
            var custom = new BifrostAuthContextFactory();
            var withOverride = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton<IBifrostAuthContextFactory>(custom)
                    .BuildServiceProvider(),
            };
            var withoutServices = new DefaultHttpContext();

            // Act + Assert
            BifrostAuthContextFactory.Resolve(withOverride).Should().BeSameAs(custom);
            BifrostAuthContextFactory.Resolve(withoutServices)
                .Should().BeSameAs(BifrostAuthContextFactory.Instance);
        }

        private static ClaimsPrincipal AuthenticatedLocalPrincipal() => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Email, "alice@club.test"),
            new Claim(ClaimTypes.Role, "admin"),
        }, authenticationType: "local"));
    }
}

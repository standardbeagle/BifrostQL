using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// RED/GREEN coverage for OIDC claim mapping: Microsoft 365 and Google provider claim
    /// sets both normalize to the one <see cref="AppIdentity"/> contract local auth produces,
    /// the M365 tenant claim (<c>tid</c>) is mapped, and the registry selects a mapper by the
    /// principal's issuer. The equivalence test guards the core requirement that downstream
    /// security modules never see a provider difference.
    /// </summary>
    public sealed class OidcClaimMapperTests
    {
        private const string GoogleIssuer = "https://accounts.google.com";
        private const string M365Issuer = "https://login.microsoftonline.com/contoso-tenant/v2.0";

        /// <summary>A sample Google OIDC principal: sub/email/name, no tenant claim.</summary>
        private static ClaimsPrincipal GooglePrincipal() => new(new ClaimsIdentity(new[]
        {
            new Claim("iss", GoogleIssuer),
            new Claim("sub", "google-117"),
            new Claim("email", "alice@gmail.com"),
            new Claim("name", "Alice Member"),
        }, "oauth2"));

        /// <summary>A sample Microsoft 365 OIDC principal: sub/email/name plus tid and groups.</summary>
        private static ClaimsPrincipal Microsoft365Principal() => new(new ClaimsIdentity(new[]
        {
            new Claim("iss", M365Issuer),
            new Claim("sub", "m365-9f3a"),
            new Claim("email", "alice@contoso.com"),
            new Claim("name", "Alice Member"),
            new Claim("tid", "contoso-tenant"),
            new Claim("groups", "group-eng"),
            new Claim("groups", "group-leads"),
        }, "oauth2"));

        [Fact]
        public void GoogleMapper_MapsSubEmailName_ToAppIdentity()
        {
            // Arrange
            var mapper = new GoogleClaimMapper();

            // Act
            var identity = mapper.Map(GooglePrincipal());

            // Assert
            identity.Id.Should().Be("google-117");
            identity.Provider.Should().Be("oidc:google");
            identity.Email.Should().Be("alice@gmail.com");
            identity.DisplayName.Should().Be("Alice Member");
            identity.TenantId.Should().BeNull("Google issues no tenant claim by default");
            identity.OrgIds.Should().BeEmpty();
        }

        [Fact]
        public void Microsoft365Mapper_MapsTidToTenant_AndGroupsToOrgIds()
        {
            // Arrange
            var mapper = new Microsoft365ClaimMapper();

            // Act
            var identity = mapper.Map(Microsoft365Principal());

            // Assert
            identity.Id.Should().Be("m365-9f3a");
            identity.Provider.Should().Be("oidc:microsoft365");
            identity.Email.Should().Be("alice@contoso.com");
            identity.DisplayName.Should().Be("Alice Member");
            identity.TenantId.Should().Be("contoso-tenant");
            identity.OrgIds.Should().BeEquivalentTo("group-eng", "group-leads");
        }

        [Fact]
        public void GoogleAndMicrosoft365_ProduceEquivalentAppIdentityShapes()
        {
            // Arrange: claim sets that carry the same logical user through each provider's
            // own claim shape. After mapping the resulting AppIdentity must be equivalent
            // for the fields the security modules read.
            var googleMapper = new GoogleClaimMapper(
                new OidcClaimMapping { TenantClaimType = "hd", GroupsClaimType = "groups" });
            var google = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("iss", GoogleIssuer),
                new Claim("sub", "shared-user"),
                new Claim("email", "user@club.test"),
                new Claim("name", "Shared User"),
                new Claim("hd", "club-7"),
                new Claim("groups", "org-a"),
                new Claim(ClaimTypes.Role, "admin"),
            }, "oauth2"));
            var m365Mapper = new Microsoft365ClaimMapper();
            var m365 = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("iss", M365Issuer),
                new Claim("sub", "shared-user"),
                new Claim("email", "user@club.test"),
                new Claim("name", "Shared User"),
                new Claim("tid", "club-7"),
                new Claim("groups", "org-a"),
                new Claim(ClaimTypes.Role, "admin"),
            }, "oauth2"));

            // Act
            var googleIdentity = googleMapper.Map(google);
            var m365Identity = m365Mapper.Map(m365);

            // Assert: identical except the diagnostic-only Provider tag.
            googleIdentity.Should().BeEquivalentTo(m365Identity, opts => opts.Excluding(i => i.Provider));
        }

        [Fact]
        public void Mapper_NoSubjectClaim_Throws()
        {
            // Arrange
            var mapper = new GoogleClaimMapper();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("iss", GoogleIssuer),
                new Claim("email", "nobody@gmail.com"),
            }, "oauth2"));

            // Act / Assert
            Assert.Throws<ArgumentException>(() => mapper.Map(principal));
        }

        [Fact]
        public void Mapper_NullPrincipal_Throws()
        {
            var mapper = new Microsoft365ClaimMapper();

            Assert.Throws<ArgumentNullException>(() => mapper.Map(null!));
        }

        [Fact]
        public void Mapper_ReadsAspNetMappedClaimTypeFallbacks()
        {
            // Arrange: a principal whose subject/email/name arrived already rewritten to the
            // ClaimTypes URIs by ASP.NET inbound claim mapping.
            var mapper = new GoogleClaimMapper();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("iss", GoogleIssuer),
                new Claim(ClaimTypes.NameIdentifier, "google-mapped"),
                new Claim(ClaimTypes.Email, "mapped@gmail.com"),
                new Claim(ClaimTypes.Name, "Mapped User"),
            }, "oauth2"));

            // Act
            var identity = mapper.Map(principal);

            // Assert
            identity.Id.Should().Be("google-mapped");
            identity.Email.Should().Be("mapped@gmail.com");
            identity.DisplayName.Should().Be("Mapped User");
        }

        [Fact]
        public void Registry_SelectsMapperByIssuerClaim()
        {
            // Arrange
            var registry = new OidcClaimMapperRegistry(new OidcClaimMapperBuilder()
                .AddGoogle(GoogleIssuer)
                .AddMicrosoft365(M365Issuer)
                .Build());

            // Act
            var googleMapper = registry.ResolveFor(GooglePrincipal());
            var m365Mapper = registry.ResolveFor(Microsoft365Principal());

            // Assert
            googleMapper.Should().BeOfType<GoogleClaimMapper>();
            m365Mapper.Should().BeOfType<Microsoft365ClaimMapper>();
        }

        [Fact]
        public void Registry_UnknownIssuer_ReturnsNull()
        {
            // Arrange
            var registry = new OidcClaimMapperRegistry(new OidcClaimMapperBuilder()
                .AddGoogle(GoogleIssuer)
                .Build());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("iss", "https://unknown-provider.test"),
                new Claim("sub", "x"),
            }, "oauth2"));

            // Act
            var mapper = registry.ResolveFor(principal);

            // Assert: a local-auth principal (no issuer) or unknown issuer maps through the
            // local claim path instead, so the registry returns null here.
            mapper.Should().BeNull();
        }

        [Fact]
        public void Registry_LocalPrincipalWithoutIssuer_ReturnsNull()
        {
            var registry = new OidcClaimMapperRegistry(new OidcClaimMapperBuilder()
                .AddGoogle(GoogleIssuer)
                .Build());
            var localPrincipal = LocalAuthEndpoint.BuildPrincipal(
                new AppIdentity(id: "local-1", provider: "local"));

            registry.ResolveFor(localPrincipal).Should().BeNull();
        }

        [Fact]
        public void Registry_DuplicateIssuer_Throws()
        {
            var builder = new OidcClaimMapperBuilder()
                .AddGoogle(GoogleIssuer)
                .AddMicrosoft365(GoogleIssuer);

            Assert.Throws<ArgumentException>(() => new OidcClaimMapperRegistry(builder.Build()));
        }

        [Fact]
        public void OidcIdentity_RoundTripsThroughBifrostContext_ToSameUserContextKeysAsLocal()
        {
            // Arrange: an M365 OIDC login mapped to AppIdentity, re-issued in the shared
            // local-auth claim shape (what UseUiAuth does), then reconstructed and projected
            // to the UserContext exactly the way the live pipeline does for local logins.
            var oidcIdentity = new Microsoft365ClaimMapper().Map(Microsoft365Principal());
            var normalizedPrincipal = LocalAuthEndpoint.BuildPrincipal(oidcIdentity);

            // Act
            var rebuilt = BifrostContext.BuildAppIdentity(normalizedPrincipal);
            var userContext = new IdentityContextMapper().ToUserContext(rebuilt);

            // Assert: the OIDC login lands on the identical UserContext keys local auth lands on.
            rebuilt.Should().BeEquivalentTo(oidcIdentity);
            userContext[MetadataKeys.Auth.DefaultUserAuditKey].Should().Be("m365-9f3a");
            userContext[MetadataKeys.Auth.DefaultTenantContextKey].Should().Be("contoso-tenant");
        }

        [Fact]
        public void BuildAppIdentity_WithOidcMapper_DelegatesToMapper()
        {
            // Arrange
            var principal = GooglePrincipal();
            var mapper = new GoogleClaimMapper();

            // Act
            var identity = BifrostContext.BuildAppIdentity(principal, mapper);

            // Assert
            identity.Provider.Should().Be("oidc:google");
            identity.Id.Should().Be("google-117");
        }
    }
}

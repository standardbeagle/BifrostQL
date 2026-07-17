using System.Security.Claims;
using BifrostQL.Server.Auth;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// The OData auth seam: Bearer (via <see cref="HttpContext.User"/>) and Basic (via the
    /// credential store) both project through the shared
    /// <see cref="IBifrostAuthContextFactory"/>, and every invalid/absent-credential path fails
    /// closed with a protocol-appropriate 401/403 — never a degraded/anonymous context.
    /// </summary>
    public sealed class ODataAuthenticatorTests
    {
        private static ODataAuthenticator Build(IODataBasicCredentialStore? store = null)
            => new(BifrostAuthContextFactory.Instance, store);

        private static ODataAuthenticator BuildWithUser()
            => Build(new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal()));

        private static async Task<ODataProtocolException> AuthShouldThrow(ODataAuthenticator auth, HttpContext ctx)
        {
            var act = () => auth.AuthenticateAsync(ctx, CancellationToken.None);
            return (await act.Should().ThrowAsync<ODataProtocolException>()).Which;
        }

        [Fact]
        public async Task Bearer_principal_projects_through_the_shared_factory()
        {
            // A Bearer token's principal is already on HttpContext.User (auth middleware ran).
            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal("bearer-sub") };

            var userContext = await BuildWithUser().AuthenticateAsync(ctx, CancellationToken.None);

            userContext.Should().NotBeEmpty("a verified request must yield a projected identity");
            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
            userContext["user"].Should().BeSameAs(ctx.User);
        }

        [Fact]
        public async Task Valid_basic_credentials_project_through_the_shared_factory()
        {
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal("basic-sub"));
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, ODataTestAuth.Password);

            var userContext = await Build(store).AuthenticateAsync(ctx, CancellationToken.None);

            userContext.Should().NotBeEmpty();
            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
        }

        [Fact]
        public async Task Anonymous_request_fails_closed_with_401()
        {
            // No Authorization header and an anonymous HttpContext.User.
            var ctx = new DefaultHttpContext();

            var ex = await AuthShouldThrow(BuildWithUser(), ctx);
            ex.HttpStatus.Should().Be(401);
            ex.Code.Should().Be("Unauthorized");
        }

        [Fact]
        public async Task Wrong_basic_password_fails_closed_with_401()
        {
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal());
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, "wrong-password");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Unknown_basic_username_fails_closed_with_401()
        {
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal());
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader("nobody", ODataTestAuth.Password);

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Disabled_basic_credential_fails_the_same_as_unknown()
        {
            // A disabled credential must be indistinguishable from an unknown one (fail closed).
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal(), enabled: false);
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, ODataTestAuth.Password);

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Basic_request_without_a_store_fails_closed_with_401()
        {
            // Basic is optional; a Bearer-only deployment registers no store, so a Basic
            // request must fail closed rather than degrade to anonymous.
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, ODataTestAuth.Password);

            var ex = await AuthShouldThrow(Build(store: null), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Malformed_basic_header_fails_closed_with_401()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = "Basic not-valid-base64!!";

            var ex = await AuthShouldThrow(BuildWithUser(), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Subjectless_identity_fails_closed_with_403_after_valid_credentials()
        {
            // A correct password that maps to a principal with no subject claim must NOT degrade
            // to an anonymous context — it fails closed as 403 (authenticated but unacceptable).
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.SubjectlessPrincipal());
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, ODataTestAuth.Password);

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(403);
            ex.Code.Should().Be("Forbidden");
        }

        [Fact]
        public async Task Unmapped_oidc_issuer_fails_closed_with_403()
        {
            // A Bearer principal whose issuer has no registered claim mapper must fail closed,
            // not silently project through the local claim path (dropping tenant/role claims).
            var ctx = new DefaultHttpContext
            {
                User = ODataTestAuth.UnmappedIssuerPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()))
                    .BuildServiceProvider(),
            };

            var ex = await AuthShouldThrow(BuildWithUser(), ctx);
            ex.HttpStatus.Should().Be(403);
        }
    }
}

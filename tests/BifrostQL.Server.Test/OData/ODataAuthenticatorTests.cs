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

        /// <summary>
        /// LOW bundle item 3: the credential record must not carry a plaintext-equivalent
        /// shared secret. The authenticator compares SHA-256(secret) to SHA-256(password),
        /// which forces every store to hold the password itself. The contract must carry a
        /// one-way password hash instead (PasswordHasher-style, mirroring LocalUserStore).
        /// </summary>
        [Fact]
        public void Credential_contract_carries_no_plaintext_equivalent_secret()
        {
            typeof(ODataBasicCredential).GetProperties().Select(p => p.Name)
                .Should().NotContain("Secret",
                    "a store holding 'Secret' holds a plaintext-equivalent: the authenticator "
                    + "must verify against a one-way hash, not compare digests of a shared secret");
        }

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

        /// <summary>
        /// LOW bundle item 3 (positive half): a store holding ONLY a one-way hash — the
        /// plaintext password never enters the credential record — authenticates the right
        /// password and rejects the wrong one.
        /// </summary>
        [Fact]
        public async Task Hash_only_credential_authenticates_the_right_password_only()
        {
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
            var hashOnly = new ODataBasicCredential(
                ODataTestAuth.Username,
                hasher.HashPassword(ODataTestAuth.Username, ODataTestAuth.Password),
                ODataTestAuth.Principal("hash-sub"),
                Enabled: true);
            var store = new HashOnlyStore(hashOnly);
            var auth = Build(store);

            var ok = new DefaultHttpContext();
            ok.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, ODataTestAuth.Password);
            (await auth.AuthenticateAsync(ok, CancellationToken.None)).Should().NotBeEmpty();

            var wrong = new DefaultHttpContext();
            wrong.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, "wrong-password");
            await AuthShouldThrow(auth, wrong);
        }

        private sealed class HashOnlyStore : IODataBasicCredentialStore
        {
            private readonly ODataBasicCredential _credential;
            public HashOnlyStore(ODataBasicCredential credential) => _credential = credential;
            public Task<ODataBasicCredential?> FindAsync(string username, CancellationToken cancellationToken)
                => Task.FromResult(username == _credential.Username ? _credential : null);
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

        /// <summary>
        /// Invariant 2 (protocol-adapter-security.md): the hash verification runs
        /// UNCONDITIONALLY. An unknown and a disabled username must each spend exactly one
        /// VerifyHashedPassword, the same as a known one — a guard that returns before the
        /// verification turns the PBKDF2 cost into an account-existence timing oracle. The
        /// counting hasher pins the call count rather than wall-clock, so the fact is
        /// deterministic; a gated compare goes RED with 0 calls.
        /// </summary>
        [Theory]
        [InlineData("nobody", true)]
        [InlineData(ODataTestAuth.Username, false)]
        public async Task Unknown_or_disabled_username_still_runs_one_hash_verification(string username, bool enabled)
        {
            var hasher = new CountingPasswordHasher();
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal(), enabled: enabled);
            var auth = new ODataAuthenticator(BifrostAuthContextFactory.Instance, store, passwordHasher: hasher);
            hasher.VerifyCalls = 0; // discard the constructor's dummy-hash provisioning

            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(username, ODataTestAuth.Password);

            await AuthShouldThrow(auth, ctx);
            hasher.VerifyCalls.Should().Be(1,
                "the existence/enabled check is ANDed after the verification, never gated before it");
        }

        private sealed class CountingPasswordHasher : Microsoft.AspNetCore.Identity.IPasswordHasher<string>
        {
            private readonly Microsoft.AspNetCore.Identity.PasswordHasher<string> _inner = new();
            public int VerifyCalls;

            public string HashPassword(string user, string password) => _inner.HashPassword(user, password);

            public Microsoft.AspNetCore.Identity.PasswordVerificationResult VerifyHashedPassword(
                string user, string hashedPassword, string providedPassword)
            {
                VerifyCalls++;
                return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            }
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

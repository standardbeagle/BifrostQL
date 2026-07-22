using System.Security.Claims;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Feeds;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Feeds
{
    /// <summary>
    /// The feed auth seam: a Bearer-authenticated request projects <see cref="HttpContext.User"/>,
    /// and a scoped feed token resolves through the host-supplied <see cref="IFeedCredentialStore"/>
    /// into a candidate principal + table allow-list — both projected through the SAME
    /// <see cref="IBifrostAuthContextFactory"/>. Every failure class (missing, unknown,
    /// disabled/revoked, expired, table-mismatched, unmapped principal) fails closed with ONE
    /// uniform 401 and mints no user context.
    /// </summary>
    public sealed class FeedAuthenticatorTests
    {
        private const string Table = "public.posts";
        private const string OtherTable = "public.secrets";

        private static FeedAuthenticator Build(IFeedCredentialStore? store = null)
            => new(BifrostAuthContextFactory.Instance, store);

        private static HttpContext ContextWithToken(string token)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.QueryString = new QueryString("?token=" + Uri.EscapeDataString(token));
            return ctx;
        }

        private static ClaimsPrincipal Principal(string subject = "feed-sub", string? tenant = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
            if (tenant is not null) claims.Add(new Claim(LocalAuthClaims.Tenant, tenant));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "feed"));
        }

        /// <summary>A principal that authenticates but carries no subject — must fail closed on projection.</summary>
        private static ClaimsPrincipal SubjectlessPrincipal()
            => new(new ClaimsIdentity(Array.Empty<Claim>(), authenticationType: "feed"));

        private static async Task<FeedAuthException> AuthShouldThrow(
            FeedAuthenticator auth, HttpContext ctx, string table = Table)
        {
            var act = () => auth.AuthenticateAsync(ctx, table, CancellationToken.None);
            return (await act.Should().ThrowAsync<FeedAuthException>()).Which;
        }

        // ---- Bearer path -----------------------------------------------------------------------

        [Fact]
        public async Task Bearer_principal_projects_through_the_shared_factory()
        {
            // A Bearer token's principal is already on HttpContext.User (host auth middleware ran).
            var ctx = new DefaultHttpContext { User = Principal("bearer-sub") };

            var userContext = await Build().AuthenticateAsync(ctx, Table, CancellationToken.None);

            userContext.Should().NotBeEmpty("a verified request must yield a projected identity");
            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
            userContext["user"].Should().BeSameAs(ctx.User);
        }

        // ---- Scoped feed-token path ------------------------------------------------------------

        [Fact]
        public async Task Scoped_token_projects_candidate_principal_through_the_shared_factory()
        {
            var store = new FakeFeedCredentialStore().Add("tok", Principal("scoped-sub"), Table);
            var ctx = ContextWithToken("tok");

            var userContext = await Build(store).AuthenticateAsync(ctx, Table, CancellationToken.None);

            userContext.Should().NotBeEmpty();
            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
        }

        [Fact]
        public async Task Scoped_token_carries_tenant_scope_into_the_resulting_context()
        {
            // The candidate principal's tenant claim must project to the tenant_id context key the
            // TenantFilterTransformer reads downstream — so a scoped feed is tenant-filtered by the
            // resulting context, not by anything the token or query supplied.
            var store = new FakeFeedCredentialStore().Add("tok", Principal("scoped-sub", tenant: "tenant-42"), Table);
            var ctx = ContextWithToken("tok");

            var userContext = await Build(store).AuthenticateAsync(ctx, Table, CancellationToken.None);

            userContext.Should().ContainKey("tenant_id");
            userContext["tenant_id"].Should().Be("tenant-42");
        }

        [Fact]
        public async Task Token_in_the_bearer_header_is_accepted_as_a_feed_token()
        {
            // A feed reader may present the scoped token as a Bearer header the host did NOT validate
            // as a JWT (HttpContext.User stays anonymous); it still resolves through the store.
            var store = new FakeFeedCredentialStore().Add("tok", Principal("scoped-sub"), Table);
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = "Bearer tok";

            var userContext = await Build(store).AuthenticateAsync(ctx, Table, CancellationToken.None);

            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
        }

        // ---- Fail-closed classes (all uniform 401) ---------------------------------------------

        [Fact]
        public async Task Missing_token_and_no_store_fails_closed_with_401()
        {
            var ctx = new DefaultHttpContext();

            var ex = await AuthShouldThrow(Build(store: null), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Token_present_but_no_store_fails_closed_with_401()
        {
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store: null), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Unknown_token_fails_closed_with_401()
        {
            var store = new FakeFeedCredentialStore().Add("tok", Principal(), Table);
            var ctx = ContextWithToken("not-the-token");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Revoked_token_is_denied()
        {
            // Modelled as a disabled credential in the store; must fail like an unknown one.
            var store = new FakeFeedCredentialStore().Add("tok", Principal(), Table, enabled: false);
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Revoked_at_source_token_resolving_to_null_is_denied()
        {
            // The other revocation shape: the store resolves the token to null.
            var store = new FakeFeedCredentialStore(); // knows no tokens
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Expired_token_is_denied()
        {
            var store = new FakeFeedCredentialStore().Add(
                "tok", Principal(), Table, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Table_not_in_the_allow_list_is_denied()
        {
            // The token is valid and enabled but scoped to a different table than the one requested.
            var store = new FakeFeedCredentialStore().Add("tok", Principal(), OtherTable);
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store), ctx, table: Table);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Empty_allow_list_matches_no_table()
        {
            var store = new FakeFeedCredentialStore().Add("tok", Principal()); // no tables
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Unmapped_principal_fails_closed()
        {
            // A resolved credential whose principal carries no subject claim must not degrade to an
            // anonymous context — it fails closed on projection with the same uniform 401.
            var store = new FakeFeedCredentialStore().Add("tok", SubjectlessPrincipal(), Table);
            var ctx = ContextWithToken("tok");

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        [Fact]
        public async Task Anonymous_request_with_neither_bearer_nor_token_fails_closed()
        {
            var store = new FakeFeedCredentialStore().Add("tok", Principal(), Table);
            var ctx = new DefaultHttpContext();

            var ex = await AuthShouldThrow(Build(store), ctx);
            ex.HttpStatus.Should().Be(401);
        }

        // ---- Anti-oracle: every failure class is byte-identical --------------------------------

        [Fact]
        public async Task Every_failure_class_produces_a_byte_identical_401_surface()
        {
            // Non-vacuous uniform-surface assertion: distinct failure causes (unknown, revoked,
            // expired, table-mismatch, subjectless, missing) must be indistinguishable — same status
            // AND same message — so no failure class is a validity/existence oracle.
            var store = new FakeFeedCredentialStore()
                .Add("revoked", Principal(), Table, enabled: false)
                .Add("expired", Principal(), Table, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))
                .Add("wrong-table", Principal(), OtherTable)
                .Add("subjectless", SubjectlessPrincipal(), Table);
            var auth = Build(store);

            var failures = new[]
            {
                await AuthShouldThrow(auth, ContextWithToken("unknown")),
                await AuthShouldThrow(auth, ContextWithToken("revoked")),
                await AuthShouldThrow(auth, ContextWithToken("expired")),
                await AuthShouldThrow(auth, ContextWithToken("wrong-table")),
                await AuthShouldThrow(auth, ContextWithToken("subjectless")),
                await AuthShouldThrow(auth, new DefaultHttpContext()),
            };

            failures.Select(f => f.HttpStatus).Distinct().Should().ContainSingle().Which.Should().Be(401);
            failures.Select(f => f.Message).Distinct().Should().ContainSingle(
                "every failure class must present one byte-identical message");
        }

        [Fact]
        public async Task Failure_message_never_carries_the_token_value()
        {
            var store = new FakeFeedCredentialStore().Add("known", Principal(), Table);
            var ctx = ContextWithToken("s3cr3t-feed-token");

            var ex = await AuthShouldThrow(Build(store), ctx);

            ex.Message.Should().NotContain("s3cr3t-feed-token");
        }

        // ---- fakes -----------------------------------------------------------------------------

        /// <summary>In-memory feed credential store; unknown tokens resolve to null (never a fallback).</summary>
        private sealed class FakeFeedCredentialStore : IFeedCredentialStore
        {
            private readonly Dictionary<string, FeedCredential> _credentials = new(StringComparer.Ordinal);

            public FakeFeedCredentialStore Add(
                string token, ClaimsPrincipal principal, string? table = null,
                bool enabled = true, DateTimeOffset? expiresAt = null)
            {
                var tables = table is null ? Array.Empty<string>() : new[] { table };
                _credentials[token] = new FeedCredential(principal, tables, enabled, expiresAt);
                return this;
            }

            public Task<FeedCredential?> ResolveAsync(string token, CancellationToken cancellationToken)
                => Task.FromResult(_credentials.TryGetValue(token, out var credential) ? credential : null);
        }
    }
}

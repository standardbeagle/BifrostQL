using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Feeds
{
    /// <summary>
    /// Conditional-GET conformance over the real pipeline: a validator (<c>ETag</c> + <c>Last-Modified</c>)
    /// derived only from the authorized, transformer-filtered result set and the request representation,
    /// and a <c>304 Not Modified</c> when the caller's precondition matches. The security-critical fact is
    /// cross-tenant NON-reuse: the ETag folds an identity partition, so tenant A's validator can never let
    /// tenant B revalidate — a shared cache cannot serve A's representation to B
    /// (.claude/rules/protocol-adapter-security.md invariant 11 corollary i).
    /// </summary>
    public sealed class FeedConditionalConformanceTests
    {
        [Fact]
        public async Task If_none_match_with_the_current_etag_yields_304_with_no_body()
        {
            await using var host = await FeedHost.StartAsync();

            var first = await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin");
            var etag = first.Headers.ETag!.ToString();
            etag.Should().NotBeNullOrWhiteSpace();

            var conditional = await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin", ifNoneMatch: etag);

            conditional.StatusCode.Should().Be(HttpStatusCode.NotModified);
            conditional.Headers.ETag!.ToString().Should().Be(etag, "the validator is still returned on a 304");
            (await conditional.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task If_modified_since_the_last_modified_instant_yields_304()
        {
            await using var host = await FeedHost.StartAsync();

            var first = await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin");
            var lastModified = first.Content.Headers.LastModified!.Value.ToString("r");

            var conditional = await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin", ifModifiedSince: lastModified);

            conditional.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }

        [Fact]
        public async Task A_different_result_set_changes_the_validator()
        {
            await using var host = await FeedHost.StartAsync();

            var full = (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Headers.ETag!.ToString();
            var filtered = (await host.GetAsync("/posts.rss?since=2026-05-04T00:00:00Z", user: "u1", tenant: "A", roles: "admin")).Headers.ETag!.ToString();

            // A narrower authorized set is a different representation → a different strong validator, so a
            // stale If-None-Match cannot force a spurious 304.
            filtered.Should().NotBe(full);
        }

        [Fact]
        public async Task Tenant_a_validator_cannot_revalidate_tenant_b_and_the_two_never_collide()
        {
            await using var host = await FeedHost.StartAsync();

            var aEtag = (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Headers.ETag!.ToString();
            var bResponse = await host.GetAsync("/posts.rss", user: "u2", tenant: "B");
            var bEtag = bResponse.Headers.ETag!.ToString();

            // Different principals never share an ETag even on overlapping content (both sets contain a
            // "Newest" row) — the identity partition is folded into the validator.
            aEtag.Should().NotBe(bEtag);

            // Tenant B presenting tenant A's ETag as a precondition must NOT get a 304 — no cross-tenant
            // cache reuse. B gets a fresh 200 of its own representation.
            var crossTenant = await host.GetAsync("/posts.rss", user: "u2", tenant: "B", ifNoneMatch: aEtag);
            crossTenant.StatusCode.Should().Be(HttpStatusCode.OK);
            crossTenant.Headers.ETag!.ToString().Should().Be(bEtag);
        }

        [Fact]
        public async Task Two_tokens_for_the_same_principal_share_a_validator()
        {
            await using var host = await FeedHost.StartAsync();
            var principal = FeedHost.Principal("svc-b", tenant: "B");
            host.Tokens.Add("tok-1", principal, "posts");
            host.Tokens.Add("tok-2", principal, "posts");

            var one = (await host.GetAsync("/posts.rss?token=tok-1")).Headers.ETag!.ToString();
            var two = (await host.GetAsync("/posts.rss?token=tok-2")).Headers.ETag!.ToString();

            // The validator carries no token material — only the projected identity partition — so two
            // distinct tokens that map to the SAME principal produce the SAME ETag (cache-shareable),
            // while the cross-tenant test proves DIFFERENT principals never collide.
            one.Should().Be(two);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Feeds
{
    /// <summary>
    /// The feed front door's two identity paths and its fail-closed 401 surface, end to end. Bearer
    /// identity is projected by the REAL shared auth-context factory; a host-owned revocable feed token
    /// resolves a <c>?token=</c> credential. Every failure class — no credential, unknown table, revoked
    /// token — must be BYTE-IDENTICAL on the wire (a bare 401 with a <c>WWW-Authenticate: Bearer</c>
    /// challenge and no body) across GET/HEAD and the .rss/.atom/negotiated variants, so the endpoint is
    /// never an existence or credential-validity oracle
    /// (.claude/rules/protocol-adapter-security.md invariants 9/10/11 spirit).
    /// </summary>
    public sealed class FeedAuthConformanceTests
    {
        [Fact]
        public async Task Feed_token_resolves_to_its_scoped_principal()
        {
            await using var host = await FeedHost.StartAsync();
            // A host-minted token bound to a tenant-B principal, allow-listed for the posts feed.
            host.Tokens.Add("tok-b", FeedHost.Principal("svc-b", tenant: "B"), "posts");

            var response = await host.GetAsync("/posts.rss?token=tok-b");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            // The token's projected tenant scopes the pipeline: tenant B's single row, not tenant A's set.
            FeedXml.RssTitles(FeedXml.Parse(await response.Content.ReadAsStringAsync()))
                .Should().ContainSingle().Which.Should().Be("Newest");
            // A query-string token must never be retained by a shared cache.
            response.Headers.CacheControl!.ToString().Should().Contain("no-store");
        }

        [Fact]
        public async Task A_revoked_token_fails_closed_with_the_uniform_401()
        {
            await using var host = await FeedHost.StartAsync();
            host.Tokens.Add("tok-b", FeedHost.Principal("svc-b", tenant: "B"), "posts");

            // Succeeds while live — the revocation below is therefore non-vacuous.
            (await host.GetAsync("/posts.rss?token=tok-b")).StatusCode.Should().Be(HttpStatusCode.OK);

            host.Tokens.Revoke("tok-b");

            var afterRevoke = await host.GetAsync("/posts.rss?token=tok-b");
            afterRevoke.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            afterRevoke.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
            (await afterRevoke.Content.ReadAsByteArrayAsync()).Should().BeEmpty("a 401 carries no distinguishing body");
        }

        [Fact]
        public async Task A_token_scoped_to_another_table_cannot_read_this_feed()
        {
            await using var host = await FeedHost.StartAsync();
            // Allow-listed for "bulletins" only — using it on /posts must fail closed, not fall through.
            host.Tokens.Add("tok-x", FeedHost.Principal("svc-x", tenant: "A", "admin"), "bulletins");

            (await host.GetAsync("/posts.rss?token=tok-x")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Every_auth_failure_class_is_the_identical_uniform_401_across_methods_and_formats()
        {
            await using var host = await FeedHost.StartAsync();
            host.Tokens.Add("live", FeedHost.Principal("svc", tenant: "B"), "posts");
            host.Tokens.Revoke("live"); // now indistinguishable from an unknown token

            // Failure sources that must all collapse to the SAME 401: no credential at all, an unknown
            // table name, and a revoked/unknown token.
            var routes = new[]
            {
                "/posts",        // negotiated (no suffix)
                "/posts.rss",
                "/posts.atom",
                "/does-not-exist.rss",       // unknown table must 401 (auth-first), never leak 404
                "/posts.rss?token=live",     // revoked token
                "/posts.rss?token=never-minted",
            };
            var methods = new[] { HttpMethod.Get, HttpMethod.Head };

            var challenges = new List<string>();
            foreach (var method in methods)
            foreach (var route in routes)
            {
                var response = await host.Client.SendAsync(host.Request(method, route));
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {route} carries no valid credential");
                (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty($"{method} {route} 401 body must be empty");
                challenges.Add(response.Headers.WwwAuthenticate.ToString());
            }

            // Byte-identical challenge for every failure class — no oracle distinguishes them.
            challenges.Distinct().Should().ContainSingle().Which.Should().Contain("Bearer");
        }

        [Fact]
        public async Task An_authenticated_head_carries_the_get_headers_with_no_body()
        {
            await using var host = await FeedHost.StartAsync();

            var head = await host.Client.SendAsync(host.Request(HttpMethod.Head, "/posts.rss", user: "u1", tenant: "A", roles: "admin"));

            head.StatusCode.Should().Be(HttpStatusCode.OK);
            head.Content.Headers.ContentType!.ToString().Should().Be("application/rss+xml; charset=utf-8");
            head.Headers.ETag.Should().NotBeNull();
            (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty("HEAD returns headers only");
        }

        [Fact]
        public async Task A_non_get_non_head_method_is_405()
        {
            await using var host = await FeedHost.StartAsync();

            // POST is authenticated up front, so a 405 here is the method gate, not an auth failure.
            var post = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/posts.rss", user: "u1", tenant: "A", roles: "admin"));

            post.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            post.Content.Headers.Allow.Should().Contain(new[] { "GET", "HEAD" });
        }
    }
}

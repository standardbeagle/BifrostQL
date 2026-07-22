using System.Security.Claims;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Feeds;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Feeds
{
    /// <summary>
    /// The feed HTTP front door: method gating, deterministic suffix/Accept negotiation, auth-before-any-
    /// work ordering, reads through <c>FeedReadPlanner</c>/<c>IQueryIntentExecutor</c> only, conditional
    /// GET (ETag/Last-Modified, 304), principal-partitioned + token-free validators, and a single
    /// sanitized error funnel with no existence oracle. Fixtures span single-column PK, composite PK, and
    /// a PK value of 0 (.claude/rules/protocol-adapter-security.md invariant 8).
    /// </summary>
    public sealed class FeedMiddlewareTests
    {
        private const string Table = "posts";
        private static readonly FeedOptions Feed = new()
        {
            MaxItems = 10,
            DefaultItems = 5,
            Title = "My Feed",
            Link = "https://example.test/feed",
            Description = "A test feed",
            Author = "Feed Operator",
        };

        // ================= method gating =================

        [Fact]
        public async Task Post_is_rejected_with_405_and_an_allow_header()
        {
            var ctx = await Run(Method: "POST", Path: "/posts.rss", bearer: Principal());

            ctx.Response.StatusCode.Should().Be(405);
            ctx.Response.Headers.Allow.ToString().Should().Contain("GET").And.Contain("HEAD");
        }

        [Fact]
        public async Task Get_and_head_are_accepted()
        {
            var get = await Run(Method: "GET", Path: "/posts.rss", bearer: Principal());
            var head = await Run(Method: "HEAD", Path: "/posts.rss", bearer: Principal());

            get.Response.StatusCode.Should().Be(200);
            head.Response.StatusCode.Should().Be(200);
        }

        // ================= format negotiation =================

        [Fact]
        public async Task Rss_suffix_selects_rss()
        {
            var ctx = await Run(Path: "/posts.rss", bearer: Principal());
            ctx.Response.ContentType.Should().Be("application/rss+xml; charset=utf-8");
        }

        [Fact]
        public async Task Atom_suffix_selects_atom()
        {
            var ctx = await Run(Path: "/posts.atom", bearer: Principal());
            ctx.Response.ContentType.Should().Be("application/atom+xml; charset=utf-8");
        }

        [Fact]
        public async Task Accept_atom_selects_atom_when_no_suffix()
        {
            var ctx = await Run(Path: "/posts", accept: "application/atom+xml", bearer: Principal());
            ctx.Response.ContentType.Should().Be("application/atom+xml; charset=utf-8");
        }

        [Fact]
        public async Task Rss_is_the_default_when_no_suffix_and_no_atom_accept()
        {
            // An unrecognized Accept falls through to the RSS default rather than a 406.
            var ctx = await Run(Path: "/posts", accept: "application/json", bearer: Principal());
            ctx.Response.ContentType.Should().Be("application/rss+xml; charset=utf-8");
        }

        [Fact]
        public async Task Suffix_takes_precedence_over_accept()
        {
            // .rss suffix wins even though Accept asks for atom.
            var ctx = await Run(Path: "/posts.rss", accept: "application/atom+xml", bearer: Principal());
            ctx.Response.ContentType.Should().Be("application/rss+xml; charset=utf-8");
        }

        [Fact]
        public async Task An_unsupported_format_extension_is_a_not_found()
        {
            // Only .rss/.atom are format suffixes; ".json" is treated as part of the (unknown) table name
            // and resolves to the uniform not-found — the documented unsupported-format behavior.
            var ctx = await Run(Path: "/posts.json", bearer: Principal());
            ctx.Response.StatusCode.Should().Be(404);
        }

        // ================= auth-first ordering / anti-oracle =================

        [Fact]
        public async Task Unauthenticated_request_is_a_bare_401_with_no_body()
        {
            var ctx = await Run(Path: "/posts.rss"); // no bearer, no token, no store

            ctx.Response.StatusCode.Should().Be(401);
            ctx.Response.Headers.WWWAuthenticate.ToString().Should().Contain("Bearer");
            BodyOf(ctx).Should().BeEmpty("a bare 401 carries no distinguishing body");
        }

        [Fact]
        public async Task Unauthenticated_request_to_an_unknown_table_is_the_same_401_as_a_known_table()
        {
            // Auth runs BEFORE model lookup, so an anonymous caller cannot tell an existing feed from a
            // missing one — both are the identical 401. Table existence never leaks before the gate.
            var known = await Run(Path: "/posts.rss");
            var unknown = await Run(Path: "/does-not-exist.rss");

            known.Response.StatusCode.Should().Be(401);
            unknown.Response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Bearer_principal_serves_the_feed()
        {
            var ctx = await Run(Path: "/posts.rss", bearer: Principal());
            ctx.Response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task Scoped_feed_token_serves_the_feed()
        {
            var store = new FakeStore().Add("tok", Principal(), Table);
            var ctx = await Run(Path: "/posts.rss", queryToken: "tok", store: store);
            ctx.Response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task Bearer_header_takes_precedence_over_a_query_token()
        {
            // A valid Bearer principal on HttpContext.User is honored even when a (bogus) ?token= is also
            // present — the Authorization/Bearer path is preferred over the query credential.
            var store = new FakeStore(); // knows no tokens: the query token alone would 401
            var ctx = await Run(Path: "/posts.rss", bearer: Principal(), queryToken: "bogus", store: store);
            ctx.Response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task Authenticated_read_carries_the_caller_tenant_scope_into_the_intent()
        {
            // The seam must hand the projected identity (tenant_id) to the pipeline; row scoping is the
            // pipeline's job — here we prove the adapter does not drop the caller's tenant.
            QueryIntent? captured = null;
            var reads = new FakeReads(ModelWith(FeedTableFixture.Posts()), Rows())
            {
                OnExecute = (intent, _) => { captured = intent; return ResultTask(Rows()); },
            };
            await Run(Path: "/posts.rss", bearer: Principal(tenant: "tenant-42"), reads: reads);

            captured.Should().NotBeNull();
            captured!.UserContext.Should().ContainKey("tenant_id");
            captured.UserContext["tenant_id"].Should().Be("tenant-42");
        }

        // ================= 200 / HEAD =================

        [Fact]
        public async Task Get_writes_a_body_head_writes_none_but_shares_headers()
        {
            var rows = Rows(Row(("id", 7), ("published_at", Ts), ("title", "Hi"), ("body", "b"), ("slug", "hi")));
            var get = await Run(Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(FeedTableFixture.Posts(), rows));
            var head = await Run(Method: "HEAD", Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(FeedTableFixture.Posts(), rows));

            BodyOf(get).Should().NotBeEmpty();
            BodyOf(head).Should().BeEmpty("HEAD carries headers only");
            head.Response.ContentType.Should().Be(get.Response.ContentType);
            head.Response.Headers.ETag.ToString().Should().Be(get.Response.Headers.ETag.ToString());
            head.Response.ContentLength.Should().Be(get.Response.ContentLength);
        }

        // ================= conditional GET =================

        [Fact]
        public async Task If_none_match_with_the_current_etag_returns_304_with_no_body()
        {
            var rows = Rows(Row(("id", 7), ("published_at", Ts), ("title", "Hi"), ("body", "b"), ("slug", "hi")));
            var first = await Run(Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(FeedTableFixture.Posts(), rows));
            var etag = first.Response.Headers.ETag.ToString();

            var second = await Run(Path: "/posts.rss", bearer: Principal(), ifNoneMatch: etag,
                reads: ReadsFor(FeedTableFixture.Posts(), rows));

            second.Response.StatusCode.Should().Be(304);
            BodyOf(second).Should().BeEmpty();
            second.Response.Headers.ETag.ToString().Should().Be(etag);
        }

        [Fact]
        public async Task If_modified_since_at_or_after_last_modified_returns_304()
        {
            var rows = Rows(Row(("id", 7), ("published_at", Ts), ("title", "Hi"), ("body", "b"), ("slug", "hi")));
            var first = await Run(Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(FeedTableFixture.Posts(), rows));
            var lastModified = first.Response.Headers.LastModified.ToString();

            var second = await Run(Path: "/posts.rss", bearer: Principal(), ifModifiedSince: lastModified,
                reads: ReadsFor(FeedTableFixture.Posts(), rows));

            second.Response.StatusCode.Should().Be(304);
        }

        [Fact]
        public async Task Empty_feed_uses_the_deterministic_unix_epoch_last_modified_and_a_stable_etag()
        {
            // An empty authorized result set dates the feed from a fixed instant so repeated polls of an
            // unchanged empty feed are deterministic (no wall-clock defeating conditional GET).
            var first = await Run(Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(FeedTableFixture.Posts(), Rows()));
            var second = await Run(Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(FeedTableFixture.Posts(), Rows()));

            first.Response.Headers.LastModified.ToString().Should().Be("Thu, 01 Jan 1970 00:00:00 GMT");
            second.Response.Headers.ETag.ToString().Should().Be(first.Response.Headers.ETag.ToString());

            var poll = await Run(Path: "/posts.rss", bearer: Principal(),
                ifNoneMatch: first.Response.Headers.ETag.ToString(), reads: ReadsFor(FeedTableFixture.Posts(), Rows()));
            poll.Response.StatusCode.Should().Be(304);
        }

        // ================= cache partitioning / headers =================

        [Fact]
        public async Task A_query_token_response_is_marked_private_no_store()
        {
            var store = new FakeStore().Add("tok", Principal(), Table);
            var ctx = await Run(Path: "/posts.rss", queryToken: "tok", store: store);
            ctx.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
        }

        [Fact]
        public async Task A_bearer_response_is_marked_private()
        {
            var ctx = await Run(Path: "/posts.rss", bearer: Principal());
            ctx.Response.Headers.CacheControl.ToString().Should().Be("private");
        }

        // ================= validator: token-free, principal-partitioned =================

        [Fact]
        public async Task Two_tokens_for_the_same_principal_produce_the_same_etag()
        {
            // The ETag derives from content + an identity PARTITION (projected claims), never the token
            // value — so the same principal via a different token validates identically.
            var principal = Principal("same-sub");
            var store = new FakeStore().Add("tokA", principal, Table).Add("tokB", principal, Table);
            var rows = Rows(Row(("id", 1), ("published_at", Ts), ("title", "H"), ("body", "b"), ("slug", "s")));

            var a = await Run(Path: "/posts.rss", queryToken: "tokA", store: store, reads: ReadsFor(FeedTableFixture.Posts(), rows));
            var b = await Run(Path: "/posts.rss", queryToken: "tokB", store: store, reads: ReadsFor(FeedTableFixture.Posts(), rows));

            b.Response.Headers.ETag.ToString().Should().Be(a.Response.Headers.ETag.ToString());
        }

        [Fact]
        public async Task Different_principals_never_share_an_etag_even_on_identical_content()
        {
            // Revert-prove: if the ETag folded ONLY document content (not the identity partition), these
            // two principals — served byte-identical rows — would collide. Folding the partition in keeps
            // them distinct.
            var rows = Rows(Row(("id", 1), ("published_at", Ts), ("title", "H"), ("body", "b"), ("slug", "s")));
            var alice = await Run(Path: "/posts.rss", bearer: Principal("alice"), reads: ReadsFor(FeedTableFixture.Posts(), rows));
            var bob = await Run(Path: "/posts.rss", bearer: Principal("bob"), reads: ReadsFor(FeedTableFixture.Posts(), rows));

            bob.Response.Headers.ETag.ToString().Should().NotBe(alice.Response.Headers.ETag.ToString());
        }

        [Fact]
        public async Task The_etag_never_contains_the_token_value()
        {
            var store = new FakeStore().Add("s3cr3t-feed-token", Principal(), Table);
            var rows = Rows(Row(("id", 1), ("published_at", Ts), ("title", "H"), ("body", "b"), ("slug", "s")));
            var ctx = await Run(Path: "/posts.rss", queryToken: "s3cr3t-feed-token", store: store,
                reads: ReadsFor(FeedTableFixture.Posts(), rows));

            ctx.Response.Headers.ETag.ToString().Should().NotContain("s3cr3t-feed-token");
        }

        // ================= sanitized failures / no oracle =================

        [Fact]
        public async Task Unknown_table_and_read_denied_both_map_to_the_same_sanitized_404()
        {
            // An unknown table and a table-level read error (BifrostExecutionError from the read seam)
            // collapse to ONE sanitized 404 — no differential signal distinguishing "exists but denied"
            // from "unknown", and no internal detail on the wire.
            var unknown = await Run(Path: "/nope.rss", bearer: Principal());
            var denied = await Run(Path: "/posts.rss", bearer: Principal(),
                reads: ThrowingReads(FeedTableFixture.Posts(), new BifrostExecutionError("policy: table read denied")));

            unknown.Response.StatusCode.Should().Be(404);
            denied.Response.StatusCode.Should().Be(404);
            BodyOf(denied).Should().BeEmpty();
        }

        // ================= cancellation is never a 401 =================

        [Fact]
        public async Task A_read_path_cancellation_is_not_mapped_to_a_wire_error()
        {
            // The client aborted before/at dispatch; an OperationCanceledException must propagate as a
            // canceled connection — never a 401 nor any error status.
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var ctx = NewContext("GET", "/posts.rss", bearer: Principal());
            ctx.RequestAborted = cts.Token;

            await Middleware().InvokeAsync(ctx);

            ctx.Response.StatusCode.Should().Be(200, "a canceled request writes no status; the default is left untouched");
        }

        [Fact]
        public async Task A_cancellation_collapsed_into_an_auth_failure_is_not_surfaced_as_a_401()
        {
            // Revert-prove: the authenticator seam can collapse a mid-resolve cancellation into a
            // FeedAuthException. Without the middleware's "if aborted, don't 401" guard this would write a
            // spurious 401; with it, the client abort is honored and no 401 is written. RequestAborted is
            // NOT cancelled at entry (so the top guard passes) — the store cancels it during the resolve.
            var cts = new CancellationTokenSource();
            var store = new FakeStore { OnResolve = () => { cts.Cancel(); throw new OperationCanceledException(cts.Token); } };
            var ctx = NewContext("GET", "/posts.rss", queryToken: "tok");
            ctx.RequestAborted = cts.Token;

            await Middleware(store: store).InvokeAsync(ctx);

            ctx.Response.StatusCode.Should().NotBe(401, "a swallowed cancellation must not surface as a 401");
        }

        // ================= failed projection leaves HttpContext.User untouched =================

        [Fact]
        public async Task A_failed_projection_restores_the_original_principal()
        {
            // Revert-prove: without the middleware's restore, a projection-failing token would leave
            // HttpContext.User set to the (subject-less) candidate principal. The restore returns it to
            // the request's original principal, minting no context.
            var store = new FakeStore().Add("tok", SubjectlessPrincipal(), Table);
            var ctx = NewContext("GET", "/posts.rss", queryToken: "tok");
            var original = ctx.User; // capture BEFORE the middleware touches it

            await Middleware(store: store).InvokeAsync(ctx);

            ctx.Response.StatusCode.Should().Be(401);
            ctx.User.Should().BeSameAs(original, "a failed projection must leave HttpContext.User untouched");
        }

        // ================= composite PK + PK value 0 (invariant 8 fixtures) =================

        [Fact]
        public async Task Conditional_get_is_stable_over_a_composite_pk_row_with_a_zero_key_component()
        {
            // Composite PK, and a key component equal to 0 — a fixture too simple (single id=1 key) would
            // be vacuous. The item id folds every component, so a stable feed yields a stable ETag/304.
            var table = FeedTableFixture.CompositeKeyPosts();
            var rows = Rows(Row(("tenant_id", 0), ("id", 0), ("published_at", Ts), ("title", "Z"), ("body", "b"), ("slug", "z")));

            var first = await Run(Path: "/posts.rss", bearer: Principal(), reads: ReadsFor(table, rows));
            first.Response.StatusCode.Should().Be(200);
            var etag = first.Response.Headers.ETag.ToString();

            var poll = await Run(Path: "/posts.rss", bearer: Principal(), ifNoneMatch: etag, reads: ReadsFor(table, rows));
            poll.Response.StatusCode.Should().Be(304);
        }

        // ================= harness =================

        private static readonly DateTime Ts = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        private static ClaimsPrincipal Principal(string subject = "feed-sub", string? tenant = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
            if (tenant is not null) claims.Add(new Claim(LocalAuthClaims.Tenant, tenant));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "feed"));
        }

        private static ClaimsPrincipal SubjectlessPrincipal()
            => new(new ClaimsIdentity(Array.Empty<Claim>(), authenticationType: "feed"));

        private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
            => cells.ToDictionary(c => c.Key, c => c.Value);

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params IReadOnlyDictionary<string, object?>[] rows)
            => rows;

        private static QueryIntentResult Result(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
            => new() { Rows = rows, Sql = string.Empty };

        private static Task<QueryIntentResult> ResultTask(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
            => Task.FromResult(Result(rows));

        private static IDbModel ModelWith(params IDbTable[] tables)
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(tables);
            return model;
        }

        private static FakeReads ReadsFor(IDbTable table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
            => new(ModelWith(table), rows);

        private static FakeReads ThrowingReads(IDbTable table, Exception ex)
            => new(ModelWith(table), Rows()) { OnExecute = (_, __) => throw ex };

        private static DefaultHttpContext NewContext(
            string method, string path, ClaimsPrincipal? bearer = null, string? queryToken = null,
            string? accept = null, string? ifNoneMatch = null, string? ifModifiedSince = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Response.Body = new MemoryStream();
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            if (bearer is not null) ctx.User = bearer;
            if (queryToken is not null)
                ctx.Request.QueryString = new QueryString("?token=" + Uri.EscapeDataString(queryToken));
            if (accept is not null) ctx.Request.Headers.Accept = accept;
            if (ifNoneMatch is not null) ctx.Request.Headers.IfNoneMatch = ifNoneMatch;
            if (ifModifiedSince is not null) ctx.Request.Headers.IfModifiedSince = ifModifiedSince;
            return ctx;
        }

        private static FeedMiddleware Middleware(
            IFeedCredentialStore? store = null, FakeReads? reads = null, FeedOptions? feed = null)
        {
            var effectiveReads = reads ?? ReadsFor(FeedTableFixture.Posts(), Rows());
            var authenticator = new FeedAuthenticator(BifrostAuthContextFactory.Instance, store);
            var planner = new FeedReadPlanner(effectiveReads);
            var options = new FeedEndpointOptions { Enabled = true };
            return new FeedMiddleware(
                _ => Task.CompletedTask, options, feed ?? Feed, authenticator, planner, effectiveReads,
                NullLogger<FeedMiddleware>.Instance);
        }

        private static async Task<DefaultHttpContext> Run(
            string Method = "GET", string Path = "/posts.rss", ClaimsPrincipal? bearer = null,
            string? queryToken = null, string? accept = null, string? ifNoneMatch = null,
            string? ifModifiedSince = null, IFeedCredentialStore? store = null, FakeReads? reads = null,
            FeedOptions? feed = null)
        {
            var ctx = NewContext(Method, Path, bearer, queryToken, accept, ifNoneMatch, ifModifiedSince);
            await Middleware(store, reads, feed).InvokeAsync(ctx);
            return ctx;
        }

        private static string BodyOf(HttpContext ctx)
        {
            ctx.Response.Body.Position = 0;
            using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }

        private sealed class FakeReads : IQueryIntentExecutor
        {
            private readonly IDbModel _model;
            private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows;

            public FakeReads(IDbModel model, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
            {
                _model = model;
                _rows = rows;
            }

            public Func<QueryIntent, CancellationToken, Task<QueryIntentResult>>? OnExecute { get; init; }

            public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromResult(_model);

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
                => OnExecute?.Invoke(intent, cancellationToken) ?? ResultTask(_rows);
        }

        private sealed class FakeStore : IFeedCredentialStore
        {
            private readonly Dictionary<string, FeedCredential> _credentials = new(StringComparer.Ordinal);

            /// <summary>When set, the resolve throws/returns via this hook (used to simulate a mid-resolve abort).</summary>
            public Func<FeedCredential?>? OnResolve { get; init; }

            public FakeStore Add(string token, ClaimsPrincipal principal, string? table = null,
                bool enabled = true, DateTimeOffset? expiresAt = null)
            {
                var tables = table is null ? Array.Empty<string>() : new[] { table };
                _credentials[token] = new FeedCredential(principal, tables, enabled, expiresAt);
                return this;
            }

            public Task<FeedCredential?> ResolveAsync(string token, CancellationToken cancellationToken)
            {
                if (OnResolve is not null)
                    return Task.FromResult(OnResolve());
                return Task.FromResult(_credentials.TryGetValue(token, out var c) ? c : null);
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Feeds;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Integration.Test.Feeds
{
    /// <summary>
    /// A full BifrostQL host that mounts the opt-in syndication-feed front door through the REAL
    /// <see cref="BifrostFeedExtensions.AddBifrostFeeds"/> / <see cref="BifrostFeedExtensions.UseBifrostFeeds"/>
    /// registration over a seeded in-memory SQLite endpoint. Unlike the server-project unit tests (which
    /// mock <see cref="IQueryIntentExecutor"/>), every feed request here runs the WHOLE shipped read
    /// pipeline: <see cref="FeedAuthenticator"/> identity projection, then <see cref="FeedReadPlanner"/>
    /// building a programmatic <see cref="BifrostQL.Core.QueryModel.GqlObjectQuery"/> executed through the
    /// live tenant-filter / soft-delete / policy transformer chain on <see cref="IQueryIntentExecutor"/>.
    /// Nothing below the HTTP wire is stubbed.
    ///
    /// <para>Identity is injected two ways, exactly as production accepts it:</para>
    /// <list type="bullet">
    /// <item>Bearer path — a tiny test authentication middleware turns the <c>X-Feed-*</c> headers into an
    /// authenticated <see cref="ClaimsPrincipal"/> on <c>HttpContext.User</c>, which the REAL
    /// <see cref="IBifrostAuthContextFactory"/> then projects (subject → user_id, tenant claim →
    /// tenant_id, role claims → roles).</item>
    /// <item>Feed-token path — a host-owned <see cref="MutableFeedCredentialStore"/> resolves a
    /// <c>?token=</c> credential to a candidate principal; revoking a token in the store makes the same
    /// token fail closed, so a succeed→revoke→401 sequence is genuinely non-vacuous.</item>
    /// </list>
    ///
    /// No production network dependency: in-proc SQLite + the in-memory HTTP handler only.
    /// </summary>
    internal sealed class FeedHost : IAsyncDisposable
    {
        public const string EndpointPath = "/graphql";
        public const string FeedPrefix = "/feeds";
        public const string UserHeader = "X-Feed-User";
        public const string TenantHeader = "X-Feed-Tenant";
        public const string RolesHeader = "X-Feed-Roles";

        private readonly SqliteConnection _keepAlive;
        private readonly IHost _host;

        /// <summary>The host-owned, mutable feed-token store — mint and revoke tokens per test.</summary>
        public MutableFeedCredentialStore Tokens { get; }

        private FeedHost(SqliteConnection keepAlive, IHost host, MutableFeedCredentialStore tokens)
        {
            _keepAlive = keepAlive;
            _host = host;
            Tokens = tokens;
        }

        /// <summary>
        /// Starts a feed host over the shared conformance seed. <paramref name="maxItems"/> /
        /// <paramref name="defaultItems"/> are the feed's server-side ceiling and no-limit fallback, so a
        /// cap test can start a host with a small ceiling while the default host serves the whole set.
        /// </summary>
        public static async Task<FeedHost> StartAsync(int maxItems = 10, int defaultItems = 10, string name = "default")
        {
            var tokens = new MutableFeedCredentialStore();
            var connString = $"Data Source=feed_it_{name}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keepAlive = new SqliteConnection(connString);
            await keepAlive.OpenAsync();
            foreach (var sql in Seed)
            {
                await using var cmd = keepAlive.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

            var feed = new FeedOptions
            {
                MaxItems = maxItems,
                DefaultItems = defaultItems,
                Title = "Example Conformance Feed",
                Link = "https://feeds.example.test/",
                Author = "Example Operator",
                Description = "A deterministic conformance feed",
            };

            var logs = new CapturedLogs();
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureLogging(l => l.AddProvider(new CapturingLoggerProvider(logs)));
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = Metadata;
                            e.DisableAuth = true;
                        });
                    });

                    // The host owns token minting/revocation/expiry — Bifrost invents no store.
                    services.AddSingleton<IFeedCredentialStore>(tokens);

                    services.AddBifrostFeeds(feed, o =>
                    {
                        o.Endpoint = EndpointPath;
                        o.RoutePrefix = FeedPrefix;
                    });
                });
                web.Configure(app =>
                {
                    // Test bearer authentication: project the X-Feed-* headers into an authenticated
                    // principal so the REAL IBifrostAuthContextFactory does the identity projection.
                    // No header → HttpContext.User stays unauthenticated → the feed falls to the token path.
                    app.Use(async (ctx, next) =>
                    {
                        var user = ctx.Request.Headers[UserHeader].ToString();
                        if (!string.IsNullOrEmpty(user))
                            ctx.User = Principal(
                                user,
                                Nullable(ctx.Request.Headers[TenantHeader].ToString()),
                                SplitRoles(ctx.Request.Headers[RolesHeader].ToString()));
                        await next();
                    });
                    app.UseBifrostEndpoints();
                    app.UseBifrostFeeds();
                });
            });

            var host = await builder.StartAsync();
            return new FeedHost(keepAlive, host, tokens) { Logs = logs };
        }

        /// <summary>Captured server-side log records (for diagnosing sanitized 4xx/5xx responses).</summary>
        public CapturedLogs Logs { get; private init; } = new();

        public HttpClient Client => _host.GetTestClient();

        /// <summary>
        /// Builds a feed request. <paramref name="route"/> is appended under <see cref="FeedPrefix"/> and
        /// may carry its own query string (e.g. <c>"/posts.rss?since=..."</c> or <c>"/posts.rss?token=t"</c>).
        /// Bearer identity comes from the <paramref name="user"/>/<paramref name="tenant"/>/<paramref name="roles"/>
        /// headers; a token request omits <paramref name="user"/> and carries <c>?token=</c> on the route.
        /// </summary>
        public HttpRequestMessage Request(
            HttpMethod method, string route, string? user = null, string? tenant = null, string? roles = null,
            string? accept = null, string? ifNoneMatch = null, string? ifModifiedSince = null)
        {
            var req = new HttpRequestMessage(method, FeedPrefix + route);
            if (user is not null) req.Headers.TryAddWithoutValidation(UserHeader, user);
            if (tenant is not null) req.Headers.TryAddWithoutValidation(TenantHeader, tenant);
            if (roles is not null) req.Headers.TryAddWithoutValidation(RolesHeader, roles);
            if (accept is not null) req.Headers.TryAddWithoutValidation("Accept", accept);
            if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            if (ifModifiedSince is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", ifModifiedSince);
            return req;
        }

        /// <summary>Sends a bearer GET and returns the response.</summary>
        public Task<HttpResponseMessage> GetAsync(
            string route, string? user = null, string? tenant = null, string? roles = null,
            string? accept = null, string? ifNoneMatch = null, string? ifModifiedSince = null)
            => Client.SendAsync(Request(HttpMethod.Get, route, user, tenant, roles, accept, ifNoneMatch, ifModifiedSince));

        /// <summary>
        /// A local-auth principal: subject → NameIdentifier, tenant → the Bifrost tenant claim, each role
        /// → a role claim. Projected by the shared factory into user_id / tenant_id / roles.
        /// </summary>
        public static ClaimsPrincipal Principal(string subject, string? tenant = null, params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
            if (!string.IsNullOrEmpty(tenant))
                claims.Add(new Claim(LocalAuthClaims.Tenant, tenant));
            foreach (var role in roles)
                claims.Add(new Claim(ClaimTypes.Role, role));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "feed-test"));
        }

        private static string? Nullable(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string[] SplitRoles(string value)
            => string.IsNullOrEmpty(value) ? Array.Empty<string>() : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _keepAlive.DisposeAsync();
        }

        // ================= seed + metadata =================

        // Tenant "A": five posts, one soft-deleted. Two share a timestamp (2026-05-03) with distinct id
        // components (1 and 2) so the composite-PK tiebreak is observable; id 0 exercises a PK value of 0
        // (.claude/rules/protocol-adapter-security.md invariant 8 fixture rule). The hostile row carries
        // markup/CDATA/quote payloads that must serialize as inert escaped text.
        // Tenant "B": one post with content deliberately close to tenant A's so cross-tenant isolation is
        // proven by identity, not by content divergence.
        // "bulletins" is a policy-gated feed: policy-actions grants update only, so a non-admin read is
        // denied (→ sanitized 404) while an admin bypasses the policy.
        public const string HostileTitle =
            "Breaking </item></channel><script>alert('xss')</script> & \"quoted\" ]]> <![CDATA[danger]]> é";
        public const string HostileBody =
            "Body </entry><b>bold</b> & <![CDATA[x]]> ]]> end";

        private static readonly string[] Seed =
        {
            @"CREATE TABLE posts (
                tenant_id   TEXT     NOT NULL,
                id          INTEGER  NOT NULL,
                published_at datetime NOT NULL,
                title       TEXT     NOT NULL,
                body        TEXT     NULL,
                slug        TEXT     NOT NULL,
                deleted_at  datetime NULL,
                PRIMARY KEY (tenant_id, id)
            );",
            @"CREATE TABLE bulletins (
                id           INTEGER PRIMARY KEY,
                published_at datetime NOT NULL,
                title        TEXT    NOT NULL,
                body         TEXT    NOT NULL
            );",
            // Timestamps use the space-separated "yyyy-MM-dd HH:mm:ss" form Microsoft.Data.Sqlite itself
            // writes a DateTime parameter as (verified: a bound DateTime renders as "2026-05-03 06:07:08").
            // Storing the seed in that exact form makes the planner's bound `since` parameter lexically
            // consistent with stored TEXT for FULL sub-day precision — an ISO "…T…Z" seed only compares
            // correctly at whole-date boundaries (the 'T' > ' ' accident) and mis-includes a sub-day since.
            // Tenant A live rows (newest → oldest): id 5 (05-04), ids 1 & 2 (both 05-03 00:00:00), id 0 (05-01).
            "INSERT INTO posts (tenant_id, id, published_at, title, body, slug, deleted_at) VALUES " +
                "('A', 0, '2026-05-01 00:00:00', 'Alpha', 'First post body', 'alpha', NULL)," +
                "('A', 1, '2026-05-03 00:00:00', '" + Sql(HostileTitle) + "', '" + Sql(HostileBody) + "', 'hostile', NULL)," +
                "('A', 2, '2026-05-03 00:00:00', 'Gamma', 'Gamma body', 'gamma', NULL)," +
                "('A', 5, '2026-05-04 00:00:00', 'Newest', 'Newest body', 'newest', NULL)," +
                "('A', 9, '2026-04-01 00:00:00', 'Deleted Draft', 'should never appear', 'deleted', '2026-04-02 00:00:00');",
            // Tenant B row.
            "INSERT INTO posts (tenant_id, id, published_at, title, body, slug, deleted_at) VALUES " +
                "('B', 0, '2026-05-04 00:00:00', 'Newest', 'Newest body', 'newest', NULL);",
            "INSERT INTO bulletins (id, published_at, title, body) VALUES (1, '2026-05-05 00:00:00', 'Ops Bulletin', 'Bulletin body');",
        };

        private static readonly string[] Metadata =
        {
            "main.posts { feed-timestamp: published_at }",
            "main.posts { feed-title: title }",
            "main.posts { feed-body: body }",
            "main.posts { feed-link: https://feeds.example.test/posts/{slug} }",
            "main.posts { tenant-filter: tenant_id }",
            "main.posts { soft-delete: deleted_at }",
            "main.bulletins { feed-timestamp: published_at }",
            "main.bulletins { feed-title: title }",
            "main.bulletins { feed-body: body }",
            // Grants update only — a non-admin read is denied by the policy engine, an admin bypasses it.
            "main.bulletins { policy-actions: update }",
        };

        private static string Sql(string value) => value.Replace("'", "''");
    }

    /// <summary>Thread-safe capture of server log records including their exceptions.</summary>
    internal sealed class CapturedLogs
    {
        private readonly ConcurrentQueue<string> _records = new();
        public void Add(string record) => _records.Enqueue(record);
        public IReadOnlyList<string> Records => _records.ToArray();
        public string Dump() => string.Join("\n", _records);
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturedLogs _logs;
        public CapturingLoggerProvider(CapturedLogs logs) => _logs = logs;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _logs);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly CapturedLogs _logs;
            public CapturingLogger(string category, CapturedLogs logs) { _category = category; _logs = logs; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                _logs.Add($"[{logLevel}] {_category}: {formatter(state, exception)}{(exception is null ? "" : " || " + exception)}");
            }
        }
    }

    /// <summary>
    /// A host-owned <see cref="IFeedCredentialStore"/> whose tokens can be minted and revoked at runtime,
    /// so a test can prove the SAME token succeeds and then, after revocation, fails closed with the
    /// uniform 401 — the non-vacuous revocation shape (.claude/rules/regression-test-non-vacuous.md). A
    /// revoked token simply resolves to null here (indistinguishable on the wire from an unknown one).
    /// </summary>
    internal sealed class MutableFeedCredentialStore : IFeedCredentialStore
    {
        private readonly ConcurrentDictionary<string, FeedCredential> _credentials = new(StringComparer.Ordinal);

        public MutableFeedCredentialStore Add(string token, ClaimsPrincipal principal, params string[] allowedTables)
        {
            _credentials[token] = new FeedCredential(principal, allowedTables, Enabled: true);
            return this;
        }

        public void Revoke(string token) => _credentials.TryRemove(token, out _);

        public Task<FeedCredential?> ResolveAsync(string token, CancellationToken cancellationToken)
            => Task.FromResult(_credentials.TryGetValue(token, out var credential) ? credential : null);
    }
}

using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Feeds;
using BifrostQL.Server.Test.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Server.Test.Feeds
{
    /// <summary>
    /// A feed table that exists but the caller may not read answers byte for byte like a feed that
    /// does not exist (.claude/rules/protocol-adapter-security.md invariant 9: assert body equality).
    /// Driven against the real transformer pipeline, so the denial is the policy gate's own throw, not
    /// a fake read seam's.
    /// </summary>
    public sealed class FeedTableExistenceTests : IAsyncLifetime
    {
        private static readonly FeedOptions Feed = new()
        {
            Title = "Feed",
            Link = "https://example.test/feed",
            Author = "Operator",
        };

        private static readonly string[] MetadataRules =
        {
            "main.posts { feed-timestamp: created_at; feed-title: title; feed-body: title }",
            // A policy with no policy-actions allows nothing: the whole table is read-denied.
            "main.vault { feed-timestamp: created_at; feed-title: title; feed-body: title; policy-read-deny: title }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS posts",
            "DROP TABLE IF EXISTS vault",
            "CREATE TABLE posts (id INTEGER PRIMARY KEY, title TEXT NOT NULL, created_at DATETIME NOT NULL)",
            "INSERT INTO posts(id, title, created_at) VALUES (1, 'hello', '2026-05-01T00:00:00Z')",
            "CREATE TABLE vault (id INTEGER PRIMARY KEY, title TEXT NOT NULL, created_at DATETIME NOT NULL)",
            "INSERT INTO vault(id, title, created_at) VALUES (1, 'classified', '2026-05-01T00:00:00Z')",
        };

        private S3ListingRealDbHarness _harness = null!;
        private readonly ExceptionLog _log = new();

        /// <summary>Keeps the server-side exception text, which the wire withholds, for failure messages.</summary>
        private sealed class ExceptionLog : ILogger<FeedMiddleware>
        {
            private readonly StringBuilder _text = new();
            public string Text => _text.ToString();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _text.Append(logLevel).Append(' ').Append(formatter(state, exception)).Append(' ').Append(exception).Append('\n');
        }

        public async Task InitializeAsync()
            => _harness = await S3ListingRealDbHarness.StartAsync(nameof(FeedTableExistenceTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        [Theory]
        [InlineData("GET", ".rss", "")]
        [InlineData("GET", ".atom", "")]
        [InlineData("HEAD", ".rss", "")]
        [InlineData("GET", "", "?since=2026-01-01T00:00:00Z&limit=5")]
        public async Task A_read_denied_feed_answers_exactly_like_a_missing_feed(string method, string suffix, string query)
        {
            // "vault" and "ghost" have the same length, so a length or echoed name cannot differ.
            var denied = await ProbeAsync(method, "/vault" + suffix, query);
            var missing = await ProbeAsync(method, "/ghost" + suffix, query);
            var readable = await ProbeAsync(method, "/posts" + suffix, query);

            readable.Should().StartWith("200", "control: the probe reaches a feed the caller can read; logged: {0}", _log.Text);
            denied.Should().Be(missing);
        }

        private async Task<string> ProbeAsync(string method, string path, string query)
        {
            var reads = _harness.Reads;
            var middleware = new FeedMiddleware(
                _ => Task.CompletedTask,
                new FeedEndpointOptions { Enabled = true, Endpoint = "/graphql" },
                Feed,
                new FeedAuthenticator(BifrostAuthContextFactory.Instance),
                new FeedReadPlanner(reads),
                reads,
                _log);

            var ctx = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "feed-user") }, authenticationType: "feed")),
            };
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            if (query.Length > 0) ctx.Request.QueryString = new QueryString(query);
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Position = 0;
            var body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
            var headers = string.Join(";", ctx.Response.Headers
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => $"{h.Key}={h.Value}"));
            return $"{ctx.Response.StatusCode}\n{headers}\n{body}";
        }
    }
}

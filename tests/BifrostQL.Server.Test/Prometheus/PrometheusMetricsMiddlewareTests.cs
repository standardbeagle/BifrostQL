using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Server.Prometheus;
using BifrostQL.Server.Test.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// The <c>/metrics</c> HTTP front door: the credential gate is the FIRST check and every denial
    /// is uniform (absent ≡ wrong ≡ disabled → the same 401 + body, no oracle — invariants 2/3);
    /// an authorized scrape emits 0.0.4 exposition; method/body limits and cancellation bound an
    /// abusive or aborted scrape (criterion 5).
    /// </summary>
    public sealed class PrometheusMetricsMiddlewareTests
    {
        private const string Credential = "scrape-secret";

        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, amount) VALUES (1, 'west', 10.0), (2, 'east', 2.0);",
        };
        private const string SalesMetric =
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount }";

        private static PrometheusMetricsMiddleware Build(ODataRealDbHarness harness, bool armed, string? endpoint = null)
        {
            var security = new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = armed,
                ScrapeCredential = armed ? Credential : null,
            };
            var options = new PrometheusExpositionOptions { Endpoint = endpoint ?? ODataRealDbHarness.EndpointPath };
            var service = new PrometheusScrapeService(
                harness.Reads,
                new PrometheusScrapeScopeResolver(security),
                new PrometheusSeriesCollector(harness.Reads),
                options);
            return new PrometheusMetricsMiddleware(
                _ => Task.CompletedTask,
                new PrometheusScrapeGate(security),
                service,
                options,
                NullLogger<PrometheusMetricsMiddleware>.Instance);
        }

        private static DefaultHttpContext Request(
            string method = "GET", string? authorization = null, long? contentLength = null, CancellationToken aborted = default)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            if (authorization is not null)
                ctx.Request.Headers.Authorization = authorization;
            if (contentLength is not null)
                ctx.Request.ContentLength = contentLength;
            ctx.Response.Body = new MemoryStream();
            if (aborted != default)
                ctx.RequestAborted = aborted;
            return ctx;
        }

        private static string BodyOf(HttpContext ctx)
        {
            ctx.Response.Body.Position = 0;
            return new StreamReader(ctx.Response.Body).ReadToEnd();
        }

        // ---- gate first + uniform denial (absent ≡ wrong ≡ disabled) ------------------------

        [Fact]
        public async Task An_authorized_scrape_emits_004_exposition()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-ok", new[] { SalesMetric }, Seed);
            var ctx = Request(authorization: $"Bearer {Credential}");

            await Build(harness, armed: true).InvokeAsync(ctx);

            ctx.Response.StatusCode.Should().Be(200);
            ctx.Response.ContentType.Should().Be(PrometheusExpositionWriter.ContentType);
            BodyOf(ctx).Should().Contain("# TYPE sales_total gauge\n");
        }

        [Fact]
        public async Task Absent_wrong_and_disabled_credentials_all_return_the_identical_denial()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-deny", new[] { SalesMetric }, Seed);

            var absent = Request();
            await Build(harness, armed: true).InvokeAsync(absent);

            var wrong = Request(authorization: "Bearer nope");
            await Build(harness, armed: true).InvokeAsync(wrong);

            // Disabled surface with the "correct" secret presented: still denied identically.
            var disabled = Request(authorization: $"Bearer {Credential}");
            await Build(harness, armed: false).InvokeAsync(disabled);

            foreach (var ctx in new[] { absent, wrong, disabled })
            {
                ctx.Response.StatusCode.Should().Be(401);
                ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
                BodyOf(ctx).Should().Be("# unauthorized\n");
            }
        }

        [Fact]
        public async Task The_gate_is_the_first_check_before_any_collection()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-gatefirst", new[] { SalesMetric }, Seed);
            // Point the service at a bogus endpoint so ScrapeAsync would THROW (500) if it were reached.
            var ctx = Request(); // no credential

            await Build(harness, armed: true, endpoint: "/does-not-exist").InvokeAsync(ctx);

            // A clean 401 (not a 500) proves the gate short-circuited before any model/collection work.
            ctx.Response.StatusCode.Should().Be(401);
        }

        // ---- request limits + cancellation (criterion 5) -----------------------------------

        [Fact]
        public async Task A_non_get_method_is_rejected()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-method", new[] { SalesMetric }, Seed);
            var ctx = Request(method: "POST", authorization: $"Bearer {Credential}");

            await Build(harness, armed: true).InvokeAsync(ctx);

            ctx.Response.StatusCode.Should().Be(405);
        }

        [Fact]
        public async Task A_request_with_a_body_over_the_cap_is_rejected()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-body", new[] { SalesMetric }, Seed);
            var ctx = Request(authorization: $"Bearer {Credential}", contentLength: 4096);

            await Build(harness, armed: true).InvokeAsync(ctx);

            ctx.Response.StatusCode.Should().Be(413);
        }

        [Fact]
        public async Task A_cancelled_scrape_writes_no_body_and_does_not_throw()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-cancel", new[] { SalesMetric }, Seed);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var ctx = Request(authorization: $"Bearer {Credential}", aborted: cts.Token);

            var act = () => Build(harness, armed: true).InvokeAsync(ctx);

            await act.Should().NotThrowAsync();
            BodyOf(ctx).Should().BeEmpty();
        }
    }
}

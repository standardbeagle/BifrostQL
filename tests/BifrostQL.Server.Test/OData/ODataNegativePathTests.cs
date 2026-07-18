using System.Security.Claims;
using System.Text.Json;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// End-to-end negative paths through the real <see cref="ODataMiddleware"/> (slice-7 acceptance
    /// criterion 3). The unit suites already prove the individual caps — the filter parser's depth
    /// cap (<see cref="ODataFilterParser.MaxDepth"/>), <c>$top</c> overflow, the <c>$expand</c>
    /// fan-out ceiling, unknown/ambiguous names, and <see cref="ODataReadOptions"/>'s unsupported-
    /// option rejection. This suite closes the remaining e2e gap: that an adversarial <c>$filter</c>
    /// and an unsupported system option surface through the MIDDLEWARE as a bounded OData 400, never
    /// an unhandled 500. The middleware maps <see cref="ODataProtocolException"/> verbatim and
    /// sanitizes everything else to a generic 500 (invariant 3), so proving the bounded 400 also
    /// proves the cap fired as a user-facing fault rather than escaping as an internal error.
    /// </summary>
    public sealed class ODataNegativePathTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL);",
            "INSERT INTO orders(id, tenant_id, name) VALUES (1,'tenant-a','a-one');",
        };

        private static readonly string[] Metadata =
        {
            "main.orders { tenant-filter: tenant_id }",
        };

        private static ClaimsPrincipal UserA => ODataTestAuth.Principal("user-a", tenant: "tenant-a");

        [Fact]
        public async Task Oversized_nested_filter_is_a_bounded_400_never_an_unhandled_500()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("neg-filter", Metadata, Seed);

            // Nesting past the parser depth cap must be caught BEFORE recursion and surface as a clean
            // 400 through the middleware — not an unhandled fault sanitized to 500.
            var depth = ODataFilterParser.MaxDepth + 5;
            var filter = new string('(', depth) + "id eq 1" + new string(')', depth);

            var (status, _, body) = await Run(harness, "/orders", "?$filter=" + filter, UserA);

            status.Should().Be(400);
            status.Should().NotBe(500);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Theory]
        [InlineData("?$apply=groupby((name))")]  // aggregation extension — not supported
        [InlineData("?$search=anything")]          // full-text search — not supported
        [InlineData("?$compute=1 add 1 as x")]    // computed properties — not supported
        public async Task Unsupported_system_query_option_is_a_bounded_400(string queryString)
        {
            await using var harness = await ODataRealDbHarness.StartAsync("neg-opt", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/orders", queryString, UserA);

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        // ---- helpers ------------------------------------------------------------------------

        private static string? ErrorCode(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataRealDbHarness harness, string path, string queryString, ClaimsPrincipal user)
        {
            var opts = new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath };
            var authenticator = new ODataAuthenticator(BifrostQL.Server.BifrostAuthContextFactory.Instance, null);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            var middleware = new ODataMiddleware(
                next, opts, authenticator, harness.Reads, NullLogger<ODataMiddleware>.Instance);

            var ctx = new DefaultHttpContext { User = user };
            ctx.Request.Path = path;
            if (queryString.Length > 0)
                ctx.Request.QueryString = new QueryString(queryString.StartsWith('?') ? queryString : "?" + queryString);
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, body);
        }
    }
}

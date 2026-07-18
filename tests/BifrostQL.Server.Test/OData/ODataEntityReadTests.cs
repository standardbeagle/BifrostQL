using System.Text.Json;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Entity-set reads end to end through the OData middleware over a real transformer-pipeline
    /// executor on seeded SQLite. Proves reads cross <see cref="IQueryIntentExecutor"/> (so tenant
    /// isolation is unskippable and out-of-tenant rows are absent), $select/$orderby/$top/$skip map
    /// onto the query intent with a stable order and bounded page size, the response carries the
    /// OData v4 <c>@odata.context</c> with correctly typed JSON values (numbers/nulls/etc.), and the
    /// untrusted query options fail as clean OData errors — never an unhandled 500.
    /// </summary>
    public sealed class ODataEntityReadTests
    {
        private static readonly string[] Seed =
        {
            // No policy/tenant metadata → readable by everyone; mixed types incl. a nullable column.
            "CREATE TABLE Widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL, note TEXT);",
            "INSERT INTO Widgets(id, name, price, note) VALUES " +
                "(2, 'beta', 3.5, NULL), (1, 'alpha', 1.25, 'first'), (3, 'gamma', 9.0, 'third'), (4, 'delta', 4.0, NULL);",
            // Tenant-scoped table for the isolation proof.
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, name) VALUES " +
                "(1, 'tenant-a', 'a-one'), (2, 'tenant-a', 'a-two'), (3, 'tenant-b', 'b-one');",
        };

        private static readonly string[] Metadata =
        {
            "main.Orders { tenant-filter: tenant_id }",
        };

        // ---- happy path: projection, order, typing, context ---------------------------------

        [Fact]
        public async Task Reads_return_all_rows_in_stable_key_order_with_typed_json_and_context()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-all", Metadata, Seed);

            var (status, contentType, body) = await Run(harness, "/widgets");

            status.Should().Be(200);
            contentType.Should().Contain("application/json");

            var root = JsonDocument.Parse(body).RootElement;
            root.GetProperty("@odata.context").GetString().Should().EndWith("/$metadata#widgets");

            var rows = root.GetProperty("value").EnumerateArray().ToList();
            // Default order is the primary key ascending — stable regardless of insertion order.
            rows.Select(r => r.GetProperty("id").GetInt32()).Should().Equal(1, 2, 3, 4);

            // Values are JSON-typed: id a number, price a number, a NULL column a JSON null.
            var alpha = rows[0];
            alpha.GetProperty("name").ValueKind.Should().Be(JsonValueKind.String);
            alpha.GetProperty("price").GetDouble().Should().Be(1.25);
            var beta = rows[1];
            beta.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null); // nullable → JSON null
        }

        [Fact]
        public async Task Select_projects_only_named_columns_and_orderby_desc_sorts_stably()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-select", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/widgets", "?$select=id,name&$orderby=name desc");

            status.Should().Be(200);
            var root = JsonDocument.Parse(body).RootElement;
            root.GetProperty("@odata.context").GetString().Should().EndWith("#widgets(id,name)");

            var rows = root.GetProperty("value").EnumerateArray().ToList();
            rows.Select(r => r.GetProperty("name").GetString()).Should().Equal("gamma", "delta", "beta", "alpha");
            // Projection restricts the object to the selected properties only.
            rows[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name");
        }

        [Fact]
        public async Task Top_and_skip_page_deterministically()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-page", Metadata, Seed);

            var (_, _, body) = await Run(harness, "/widgets", "?$top=2&$skip=1");

            var ids = JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(r => r.GetProperty("id").GetInt32()).ToList();
            // Ordered by PK asc (1,2,3,4); skip 1, top 2 → rows 2 and 3.
            ids.Should().Equal(2, 3);
        }

        [Fact]
        public async Task Top_is_clamped_to_the_configured_maximum_page_size()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-clamp", Metadata, Seed);
            var options = new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath, MaxPageSize = 2 };

            var (_, _, body) = await Run(harness, "/widgets", "?$top=1000", options: options);

            JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Should().HaveCount(2, "a requested $top above the maximum is bounded to the ceiling");
        }

        // ---- tenant isolation: the load-bearing security property ---------------------------

        [Fact]
        public async Task Reads_are_tenant_scoped_so_other_tenants_rows_are_absent()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-tenant", Metadata, Seed);

            var (statusA, _, bodyA) = await Run(harness, "/orders",
                user: ODataTestAuth.Principal("user-a", tenant: "tenant-a"));
            var (statusB, _, bodyB) = await Run(harness, "/orders",
                user: ODataTestAuth.Principal("user-b", tenant: "tenant-b"));

            statusA.Should().Be(200);
            statusB.Should().Be(200);

            var namesA = Names(bodyA);
            var namesB = Names(bodyB);
            // The adapter built no predicate — the pipeline scoped each read from the identity's tenant.
            namesA.Should().BeEquivalentTo("a-one", "a-two");
            namesB.Should().BeEquivalentTo("b-one");
            namesA.Should().NotIntersectWith(namesB);
        }

        // ---- clean OData errors on unknown/ambiguous names and malformed options ------------

        [Fact]
        public async Task Unknown_entity_set_is_a_404()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-unknown-set", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/nosuchset");

            status.Should().Be(404);
            ErrorCode(body).Should().Be("NotFound");
        }

        [Fact]
        public async Task Unknown_select_property_is_a_400()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-unknown-prop", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/widgets", "?$select=nope");

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Theory]
        [InlineData("?$top=abc")]
        [InlineData("?$skip=-1")]
        [InlineData("?$top=99999999999999999999999999999")] // 29 digits: overflows int
        public async Task Malformed_query_options_are_clean_400s_never_an_unhandled_500(string queryString)
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-badopt", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/widgets", queryString);

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Fact]
        public async Task Duplicated_query_option_is_a_400()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-dupopt", Metadata, Seed);

            var (status, _, _) = await Run(harness, "/widgets", "?$top=1&$top=2");

            status.Should().Be(400);
        }

        [Fact]
        public async Task Expand_of_an_unknown_navigation_is_a_clean_400()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("read-expand", Metadata, Seed);

            // Widgets declares no navigations, so any $expand target is unknown → a clean 400
            // (never a 501 now that one-level $expand is supported).
            var (status, _, body) = await Run(harness, "/widgets", "?$expand=whatever");

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        // ---- harness plumbing ---------------------------------------------------------------

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataRealDbHarness harness,
            string path,
            string queryString = "",
            System.Security.Claims.ClaimsPrincipal? user = null,
            ODataOptions? options = null)
        {
            var opts = options ?? new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath };
            var authenticator = new ODataAuthenticator(BifrostAuthContextFactory.Instance, null);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            var middleware = new ODataMiddleware(
                next, opts, authenticator, harness.Reads, NullLogger<ODataMiddleware>.Instance);

            var ctx = new DefaultHttpContext { User = user ?? ODataTestAuth.Principal() };
            ctx.Request.Path = path;
            if (queryString.Length > 0)
                ctx.Request.QueryString = new QueryString(queryString);
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, body);
        }

        private static string? ErrorCode(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();

        private static IReadOnlyList<string?> Names(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(r => r.GetProperty("name").GetString()).ToList();
    }
}

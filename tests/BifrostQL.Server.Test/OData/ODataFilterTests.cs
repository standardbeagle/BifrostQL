using System.Text.Json;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// End-to-end OData <c>$filter</c> through the middleware over the real transformer-pipeline
    /// executor on seeded SQLite. Proves the parsed filter becomes a PARAMETERIZED
    /// <c>TableFilter</c> on the query intent (an injection payload in a string literal is inert —
    /// bound as data, never SQL), operator precedence / parentheses / De Morgan <c>not</c> resolve
    /// correctly against real rows, null and in/contains map to the right predicates, and — the
    /// load-bearing security property — a <c>$filter</c> AND-composes with the pipeline's tenant
    /// scope rather than replacing it. Malformed/unsupported/incompatible filters are clean OData
    /// 400s, never an unhandled 500.
    /// </summary>
    public sealed class ODataFilterTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE Widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL, note TEXT);",
            "INSERT INTO Widgets(id, name, price, note) VALUES " +
                "(1, 'alpha', 1.25, 'first'), (2, 'beta', 3.5, NULL), (3, 'gamma', 9.0, 'third'), (4, 'delta', 4.0, NULL);",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, name) VALUES " +
                "(1, 'tenant-a', 'a-one'), (2, 'tenant-a', 'a-two'), (3, 'tenant-b', 'b-one');",
        };

        private static readonly string[] Metadata =
        {
            "main.Orders { tenant-filter: tenant_id }",
        };

        // ---- injection: the load-bearing parameterization proof -----------------------------

        [Fact]
        public async Task String_literal_injection_payload_is_inert_bound_data_not_sql()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-inject", Metadata, Seed);

            // If the literal were concatenated into SQL, the trailing `OR '1'='1'` would match every
            // row. Bound as a parameter, it matches no widget named that exact string → zero rows,
            // and never a 500.
            var (status, _, body) = await Run(harness, "/widgets", "?$filter=name eq 'x'' OR ''1''=''1'");

            status.Should().Be(200);
            Ids(body).Should().BeEmpty("the payload is a single bound literal value, not SQL syntax");
        }

        // ---- comparisons, precedence, parentheses, not (De Morgan) --------------------------

        [Fact]
        public async Task Equality_on_a_string_column_returns_the_matching_row()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-eq", Metadata, Seed);
            var (status, _, body) = await Run(harness, "/widgets", "?$filter=name eq 'alpha'");
            status.Should().Be(200);
            Ids(body).Should().Equal(1);
        }

        [Fact]
        public async Task Numeric_greater_than_uses_a_bound_typed_value()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-gt", Metadata, Seed);
            var (_, _, body) = await Run(harness, "/widgets", "?$filter=price gt 3.5");
            Ids(body).Should().Equal(3, 4); // 9.0 and 4.0 are > 3.5; 3.5 itself is not
        }

        [Fact]
        public async Task And_binds_tighter_than_or()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-prec", Metadata, Seed);

            // id eq 1 or (id eq 2 and price gt 100). No row has price > 100, so only id 1 survives.
            // Were precedence wrong ((id 1 or id 2) and false) the result would be empty.
            var (_, _, body) = await Run(harness, "/widgets", "?$filter=id eq 1 or id eq 2 and price gt 100");
            Ids(body).Should().Equal(1);
        }

        [Fact]
        public async Task Parentheses_override_precedence()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-paren", Metadata, Seed);
            var (_, _, body) = await Run(harness, "/widgets", "?$filter=(id eq 1 or id eq 2) and price lt 2");
            Ids(body).Should().Equal(1); // id 2 has price 3.5 (not < 2), id 1 has 1.25
        }

        [Fact]
        public async Task Not_negates_via_de_morgan()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-not", Metadata, Seed);

            // not (id eq 1 and name eq 'alpha') = id ne 1 or name ne 'alpha' → everything but row 1.
            var (_, _, body) = await Run(harness, "/widgets", "?$filter=not (id eq 1 and name eq 'alpha')");
            Ids(body).Should().Equal(2, 3, 4);
        }

        // ---- null semantics, in, contains ---------------------------------------------------

        [Fact]
        public async Task Eq_null_and_ne_null_map_to_is_null_predicates()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-null", Metadata, Seed);

            var (_, _, nulls) = await Run(harness, "/widgets", "?$filter=note eq null");
            Ids(nulls).Should().Equal(2, 4);

            var (_, _, notNulls) = await Run(harness, "/widgets", "?$filter=note ne null");
            Ids(notNulls).Should().Equal(1, 3);
        }

        [Fact]
        public async Task In_list_matches_any_listed_value()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-in", Metadata, Seed);
            var (_, _, body) = await Run(harness, "/widgets", "?$filter=id in (1, 3)");
            Ids(body).Should().Equal(1, 3);
        }

        [Fact]
        public async Task Contains_matches_a_substring()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-contains", Metadata, Seed);
            var (_, _, body) = await Run(harness, "/widgets", "?$filter=contains(name, 'amm')");
            Ids(body).Should().Equal(3); // only 'gamma' contains 'amm'
        }

        // ---- tenant composition: $filter AND-composes, never bypasses -----------------------

        [Fact]
        public async Task Filter_and_composes_with_the_tenant_scope_and_cannot_reach_another_tenants_row()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-tenant", Metadata, Seed);

            // tenant-a filtering for one of its own rows → returned.
            var (statusOwn, _, own) = await Run(harness, "/orders", "?$filter=name eq 'a-one'",
                user: ODataTestAuth.Principal("user-a", tenant: "tenant-a"));
            statusOwn.Should().Be(200);
            Names(own).Should().Equal("a-one");

            // tenant-a filtering for a tenant-b row by name → the tenant filter is AND-composed, so
            // the row is invisible even though the $filter predicate matches it. The adapter's filter
            // narrowed, it did not replace the pipeline's scope.
            var (statusCross, _, cross) = await Run(harness, "/orders", "?$filter=name eq 'b-one'",
                user: ODataTestAuth.Principal("user-a", tenant: "tenant-a"));
            statusCross.Should().Be(200);
            Names(cross).Should().BeEmpty("the tenant scope is ANDed onto the $filter, never bypassed");
        }

        // ---- clean 400s, never a 500 --------------------------------------------------------

        [Theory]
        [InlineData("?$filter=nope eq 1")]                                   // unknown property
        [InlineData("?$filter=price add 1 gt 2")]                            // arithmetic (unsupported)
        [InlineData("?$filter=startswith(name, 'a')")]                       // unsupported function
        [InlineData("?$filter=id eq 'abc'")]                                 // type mismatch (string→int)
        [InlineData("?$filter=id eq 99999999999999999999999999999")]         // 29 digits: overflows int
        [InlineData("?$filter=name eq")]                                     // truncated
        [InlineData("?$filter=contains(id, 'x')")]                           // contains on a non-string column
        public async Task Malformed_or_unsupported_or_incompatible_filters_are_clean_400s(string queryString)
        {
            await using var harness = await ODataRealDbHarness.StartAsync("filter-bad", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/widgets", queryString);

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        // ---- harness plumbing ---------------------------------------------------------------

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataRealDbHarness harness,
            string path,
            string queryString = "",
            System.Security.Claims.ClaimsPrincipal? user = null)
        {
            var opts = new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath };
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

        private static IReadOnlyList<int> Ids(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(r => r.GetProperty("id").GetInt32()).ToList();

        private static IReadOnlyList<string?> Names(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(r => r.GetProperty("name").GetString()).ToList();
    }
}

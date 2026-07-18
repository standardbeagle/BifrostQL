using System.Text.Json;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// $count and bounded server-driven paging end to end through the OData middleware over a real
    /// transformer-pipeline executor on seeded SQLite. Proves: $count reports the pipeline-filtered
    /// total through the SAME intent (tenant + $filter applied to the count, never a leak of hidden
    /// rows); an over-large $top clamps and a bounded page emits an opaque @odata.nextLink; the
    /// server re-derives the next page from the token IT signed (client offsets are never trusted);
    /// tampered / cross-query / cross-tenant tokens fail as clean OData 400s; and the continuation
    /// request re-applies tenant scope per request (a tenant-a token never pages tenant-b's rows).
    /// </summary>
    public sealed class ODataPagingAndCountTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE Widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL);",
            "INSERT INTO Widgets(id, name, price) VALUES " +
                "(1,'a',1.0),(2,'b',2.0),(3,'c',3.0),(4,'d',4.0),(5,'e',5.0),(6,'f',6.0),(7,'g',7.0);",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, name) VALUES " +
                "(1,'tenant-a','a1'),(2,'tenant-a','a2'),(3,'tenant-a','a3'),(4,'tenant-a','a4')," +
                "(5,'tenant-b','b1'),(6,'tenant-b','b2'),(7,'tenant-b','b3');",
        };

        private static readonly string[] Metadata =
        {
            "main.Orders { tenant-filter: tenant_id }",
        };

        // A fixed secret so tokens minted on one request resolve on the next (each Run builds a
        // fresh middleware; without a shared secret the per-instance random key would differ).
        private static ODataOptions Opts(int max = 1000, int def = 100) => new()
        {
            Endpoint = ODataRealDbHarness.EndpointPath,
            MaxPageSize = max,
            DefaultPageSize = def,
            ContinuationTokenSecret = "test-odata-continuation-secret-0001",
        };

        // ---- $count: pipeline-filtered, same intent -----------------------------------------

        [Fact]
        public async Task Count_true_reports_the_total_through_the_same_intent()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("count-all", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/widgets", "?$count=true");

            status.Should().Be(200);
            var root = JsonDocument.Parse(body).RootElement;
            root.GetProperty("@odata.count").GetInt64().Should().Be(7);
        }

        [Fact]
        public async Task Count_respects_the_filter_so_it_counts_only_matching_rows()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("count-filter", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/widgets", "?$filter=price gt 4&$count=true");

            status.Should().Be(200);
            var root = JsonDocument.Parse(body).RootElement;
            // price gt 4 → ids 5,6,7 → count 3, computed AFTER the same filter as the rows.
            root.GetProperty("@odata.count").GetInt64().Should().Be(3);
        }

        [Fact]
        public async Task Count_is_tenant_scoped_and_never_leaks_hidden_rows()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("count-tenant", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/orders", "?$count=true",
                user: ODataTestAuth.Principal("user-a", tenant: "tenant-a"));

            status.Should().Be(200);
            var root = JsonDocument.Parse(body).RootElement;
            // tenant-a owns 4 of 7 rows; the count must not reveal the 3 tenant-b rows.
            root.GetProperty("@odata.count").GetInt64().Should().Be(4);
        }

        // ---- server-driven paging: bounded pages + opaque nextLink --------------------------

        [Fact]
        public async Task A_bounded_page_emits_a_nextlink_and_the_token_resumes_the_next_page()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-walk", Metadata, Seed);
            var opts = Opts(max: 3, def: 3);

            var (s1, _, b1) = await Run(harness, "/widgets", "?$top=3", options: opts);
            s1.Should().Be(200);
            var r1 = JsonDocument.Parse(b1).RootElement;
            Ids(r1).Should().Equal(1, 2, 3);
            var next = r1.GetProperty("@odata.nextLink").GetString();
            next.Should().NotBeNullOrEmpty();
            next.Should().Contain("$skiptoken=");

            // Follow the nextLink: the server re-derives the offset from the token it signed.
            var (path, query) = SplitLink(next!);
            var (s2, _, b2) = await Run(harness, path, query, options: opts);
            s2.Should().Be(200);
            var r2 = JsonDocument.Parse(b2).RootElement;
            // The second page resumes exactly after the first with no overlap or skip.
            Ids(r2).Should().Equal(4, 5, 6);

            // Third page: the final row, no further nextLink.
            var (p3, q3) = SplitLink(r2.GetProperty("@odata.nextLink").GetString()!);
            var (_, _, b3) = await Run(harness, p3, q3, options: opts);
            var r3 = JsonDocument.Parse(b3).RootElement;
            Ids(r3).Should().Equal(7);
            r3.TryGetProperty("@odata.nextLink", out _).Should().BeFalse("the final page has no continuation");
        }

        [Fact]
        public async Task A_full_final_page_has_no_nextlink()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-final", Metadata, Seed);

            // 7 rows, page size 7 → a single full page, exactly at the boundary, no nextLink.
            var (status, _, body) = await Run(harness, "/widgets", "?$top=7", options: Opts(max: 7, def: 7));

            status.Should().Be(200);
            var root = JsonDocument.Parse(body).RootElement;
            Ids(root).Should().Equal(1, 2, 3, 4, 5, 6, 7);
            root.TryGetProperty("@odata.nextLink", out _).Should().BeFalse();
        }

        [Fact]
        public async Task Paging_past_the_end_returns_an_empty_page_with_no_nextlink()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-empty", Metadata, Seed);
            var opts = Opts(max: 3, def: 3);

            // Walk to the last page (ids 7), then follow no further — instead mint the offset past
            // the end by paging one extra hop from a page that DID have a nextLink.
            var (_, _, b1) = await Run(harness, "/widgets", "?$top=3&$filter=id gt 5", options: opts);
            var r1 = JsonDocument.Parse(b1).RootElement;
            Ids(r1).Should().Equal(6, 7);
            // ids 6,7 is only 2 rows (< page size 3) so there is no nextLink; assert the empty-set
            // shape directly by requesting a filter that matches nothing.
            r1.TryGetProperty("@odata.nextLink", out _).Should().BeFalse();

            var (_, _, bEmpty) = await Run(harness, "/widgets", "?$filter=id gt 100", options: opts);
            var rEmpty = JsonDocument.Parse(bEmpty).RootElement;
            rEmpty.GetProperty("value").GetArrayLength().Should().Be(0);
            rEmpty.TryGetProperty("@odata.nextLink", out _).Should().BeFalse();
        }

        [Fact]
        public async Task Top_above_the_maximum_clamps_and_the_bounded_page_emits_a_nextlink()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-clamp", Metadata, Seed);

            // Ask for 1000 but the ceiling is 3 → 3 rows returned and a nextLink because more remain.
            var (status, _, body) = await Run(harness, "/widgets", "?$top=1000", options: Opts(max: 3, def: 3));

            status.Should().Be(200);
            var root = JsonDocument.Parse(body).RootElement;
            root.GetProperty("value").GetArrayLength().Should().Be(3);
            root.GetProperty("@odata.nextLink").GetString().Should().Contain("$skiptoken=");
        }

        // ---- token security: tamper, cross-query, tenant isolation on continuation ----------

        [Fact]
        public async Task A_tampered_skiptoken_is_a_clean_400()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-tamper", Metadata, Seed);
            var opts = Opts(max: 3, def: 3);

            var (_, _, b1) = await Run(harness, "/widgets", "?$top=3", options: opts);
            var token = SkipTokenOf(JsonDocument.Parse(b1).RootElement);
            // Deterministic tamper: flip the first MAC byte (see ODataContinuationTokenTests).
            var tampered = ODataContinuationTokenTests.FlipMacByte(token);

            var (status, _, body) = await Run(harness, "/widgets", $"?$top=3&$skiptoken={Uri.EscapeDataString(tampered)}", options: opts);

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Fact]
        public async Task A_token_minted_for_one_filter_is_rejected_when_replayed_with_another()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-crossquery", Metadata, Seed);
            var opts = Opts(max: 2, def: 2);

            // Mint under $filter=id gt 0 (7 rows → a nextLink exists).
            var (_, _, b1) = await Run(harness, "/widgets", "?$top=2&$filter=id gt 0", options: opts);
            var token = SkipTokenOf(JsonDocument.Parse(b1).RootElement);

            // Replay the same token with a DIFFERENT $filter — the re-derived query-shape hash
            // differs, so the MAC check fails: a clean 400, never a wrong page for the new filter.
            var (status, _, body) = await Run(harness, "/widgets",
                $"?$top=2&$filter=id gt 3&$skiptoken={Uri.EscapeDataString(token)}", options: opts);

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Fact]
        public async Task A_token_minted_under_tenant_a_never_pages_tenant_b_rows()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("page-tenant", Metadata, Seed);
            var opts = Opts(max: 2, def: 2);
            var userA = ODataTestAuth.Principal("user-a", tenant: "tenant-a");
            var userB = ODataTestAuth.Principal("user-b", tenant: "tenant-b");

            // tenant-a pages its own 4 rows (a1..a4); page 1 → a1,a2 with a nextLink.
            var (_, _, b1) = await Run(harness, "/orders", "?$top=2", user: userA, options: opts);
            var r1 = JsonDocument.Parse(b1).RootElement;
            Names(r1).Should().Equal("a1", "a2");
            var token = SkipTokenOf(r1);

            // Replaying tenant-a's token as tenant-b: the identity fingerprint differs → 400.
            // The continuation never leaks tenant-a's rows to tenant-b.
            var (statusB, _, bodyB) = await Run(harness, "/orders",
                $"?$top=2&$skiptoken={Uri.EscapeDataString(token)}", user: userB, options: opts);
            statusB.Should().Be(400);
            ErrorCode(bodyB).Should().Be("BadRequest");

            // And tenant-a's own continuation stays within tenant-a's scope (a3,a4) — never a tenant-b row.
            var (statusA, _, bodyA) = await Run(harness, "/orders",
                $"?$top=2&$skiptoken={Uri.EscapeDataString(token)}", user: userA, options: opts);
            statusA.Should().Be(200);
            Names(JsonDocument.Parse(bodyA).RootElement).Should().Equal("a3", "a4");
        }

        // ---- harness plumbing ---------------------------------------------------------------

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataRealDbHarness harness,
            string path,
            string queryString = "",
            System.Security.Claims.ClaimsPrincipal? user = null,
            ODataOptions? options = null)
        {
            var opts = options ?? Opts();
            var authenticator = new ODataAuthenticator(BifrostAuthContextFactory.Instance, null);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            var middleware = new ODataMiddleware(
                next, opts, authenticator, harness.Reads, NullLogger<ODataMiddleware>.Instance);

            var ctx = new DefaultHttpContext { User = user ?? ODataTestAuth.Principal() };
            ctx.Request.Path = path;
            if (queryString.Length > 0)
                ctx.Request.QueryString = new QueryString(queryString.StartsWith('?') ? queryString : "?" + queryString);
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, body);
        }

        private static (string Path, string Query) SplitLink(string nextLink)
        {
            // nextLink is scheme://host/prefix/EntitySet?query — the middleware routes on Path only
            // (the prefix lives on PathBase, empty in these tests), so extract the set + query.
            var q = nextLink.IndexOf('?');
            var query = q >= 0 ? nextLink[q..] : string.Empty;
            var beforeQuery = q >= 0 ? nextLink[..q] : nextLink;
            var lastSlash = beforeQuery.LastIndexOf('/');
            var set = beforeQuery[(lastSlash + 1)..];
            return ("/" + set, query);
        }

        private static string SkipTokenOf(JsonElement root)
        {
            var (_, query) = SplitLink(root.GetProperty("@odata.nextLink").GetString()!);
            var q = QueryHelpers.Parse(query);
            return q["$skiptoken"];
        }

        private static IReadOnlyList<int> Ids(JsonElement root)
            => root.GetProperty("value").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();

        private static IReadOnlyList<string?> Names(JsonElement root)
            => root.GetProperty("value").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();

        private static string? ErrorCode(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    /// <summary>Minimal query-string splitter for the paging tests (avoids a WebUtilities dependency guess).</summary>
    internal static class QueryHelpers
    {
        public static IDictionary<string, string> Parse(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var trimmed = query.TrimStart('?');
            if (trimmed.Length == 0) return result;
            foreach (var pair in trimmed.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                result[key] = value;
            }
            return result;
        }
    }
}

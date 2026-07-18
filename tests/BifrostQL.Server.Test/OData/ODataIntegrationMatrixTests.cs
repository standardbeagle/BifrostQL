using System.Security.Claims;
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
    /// The end-to-end OData v4 SQLite integration MATRIX (slice-7 acceptance criterion 2): every
    /// query option — <c>$select</c>/<c>$filter</c>/<c>$orderby</c>/<c>$top</c>/<c>$skip</c>/
    /// <c>$count</c>/<c>$expand</c> — is exercised through the real <see cref="ODataMiddleware"/>
    /// over a seeded SQLite database whose <c>orders</c>/<c>order_lines</c> tables carry BOTH a
    /// tenant-filter AND a soft-delete rule, so each option is proven to respect tenant isolation
    /// AND soft-delete simultaneously.
    ///
    /// <para>The load-bearing NEW proof here (beyond the per-option tenant coverage the entity-read
    /// /filter/paging/expand suites already carry) is <b>soft-delete enforcement across every
    /// option</b>: a soft-deleted row that WOULD match a $filter, WOULD sort first under $orderby,
    /// WOULD fall inside a $top/$skip window, and WOULD be counted by $count is absent from all of
    /// them — and a soft-deleted child never appears inside a $expand. The adapter builds no
    /// predicate; the pipeline ANDs <c>deleted_at IS NULL</c> and the tenant scope onto every read
    /// and every independently-scoped expansion.</para>
    /// </summary>
    public sealed class ODataIntegrationMatrixTests
    {
        // orders: a tenant-a soft-deleted row (id 9) that would otherwise win every option (highest
        // amount, sorts first desc, matches amount>5, falls in the first page, counts), plus a
        // tenant-b live row (5) and tenant-b soft-deleted row (8) to prove tenant + soft-delete
        // compose. order_lines: a soft-deleted line (7) under a VISIBLE order, and a live line (3)
        // under the soft-deleted order 9, to prove expand scoping on the child.
        private static readonly string[] Seed =
        {
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, amount REAL NOT NULL, deleted_at TEXT);",
            "INSERT INTO orders(id, tenant_id, name, amount, deleted_at) VALUES " +
                "(1,'tenant-a','a-alpha',10,NULL)," +
                "(2,'tenant-a','a-beta',20,NULL)," +
                "(3,'tenant-a','a-gamma',30,NULL)," +
                "(9,'tenant-a','a-ghost',99,'2026-01-01T00:00:00Z')," +  // soft-deleted, tenant-a
                "(5,'tenant-b','b-only',50,NULL)," +
                "(8,'tenant-b','b-ghost',88,'2026-01-01T00:00:00Z');",   // soft-deleted, tenant-b
            "CREATE TABLE order_lines (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL, tenant_id TEXT NOT NULL, sku TEXT NOT NULL, deleted_at TEXT, " +
                "FOREIGN KEY (order_id) REFERENCES orders(id));",
            "INSERT INTO order_lines(id, order_id, tenant_id, sku, deleted_at) VALUES " +
                "(1,1,'tenant-a','sku-a1',NULL)," +
                "(2,1,'tenant-a','sku-a2',NULL)," +
                "(7,1,'tenant-a','sku-ghost','2026-01-01T00:00:00Z')," +  // soft-deleted line under visible order 1
                "(3,9,'tenant-a','sku-under-ghost',NULL);",              // live line under soft-deleted order 9
        };

        private static readonly string[] Metadata =
        {
            "main.orders { tenant-filter: tenant_id; soft-delete: deleted_at }",
            "main.order_lines { tenant-filter: tenant_id; soft-delete: deleted_at }",
        };

        private static ClaimsPrincipal UserA => ODataTestAuth.Principal("user-a", tenant: "tenant-a");
        private static ClaimsPrincipal UserB => ODataTestAuth.Principal("user-b", tenant: "tenant-b");

        // ---- baseline: tenant + soft-delete on a plain collection read ----------------------

        [Fact]
        public async Task Plain_read_excludes_other_tenants_and_soft_deleted_rows()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-plain", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/orders", user: UserA);

            status.Should().Be(200);
            // 1,2,3 live tenant-a; 9 soft-deleted (absent); 5/8 tenant-b (absent).
            Ids(body).Should().Equal(1, 2, 3);

            // Same read as tenant-b sees only its own live row, never tenant-a and never its own ghost.
            var (_, _, bodyB) = await Run(harness, "/orders", user: UserB);
            Ids(bodyB).Should().Equal(5);
        }

        // ---- $select ------------------------------------------------------------------------

        [Fact]
        public async Task Select_projects_named_columns_and_still_excludes_scoped_and_deleted_rows()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-select", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/orders", "?$select=id,name", user: UserA);

            status.Should().Be(200);
            Ids(body).Should().Equal(1, 2, 3);
            JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().First()
                .EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name");
        }

        // ---- $filter: the soft-deleted row matches the predicate but must NOT surface --------

        [Fact]
        public async Task Filter_matches_are_still_soft_delete_and_tenant_scoped()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-filter", Metadata, Seed);

            // amount gt 5 matches 1,2,3 AND the soft-deleted ghost (amount 99) AND tenant-b rows —
            // only the live tenant-a rows may surface.
            var (status, _, body) = await Run(harness, "/orders", "?$filter=amount gt 5", user: UserA);

            status.Should().Be(200);
            Ids(body).Should().Equal(1, 2, 3);
        }

        // ---- $orderby: the soft-deleted row would sort first but must be absent --------------

        [Fact]
        public async Task Orderby_desc_never_surfaces_the_higher_amount_soft_deleted_row()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-order", Metadata, Seed);

            // amount desc would put the ghost (99) first if it were visible; it must not appear.
            var (status, _, body) = await Run(harness, "/orders", "?$orderby=amount desc", user: UserA);

            status.Should().Be(200);
            Ids(body).Should().Equal(3, 2, 1);
        }

        // ---- $top / $skip: the soft-deleted row must not consume a page slot ----------------

        [Fact]
        public async Task Top_and_skip_page_over_live_scoped_rows_only()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-page", Metadata, Seed);

            var (_, _, top) = await Run(harness, "/orders", "?$top=2", user: UserA);
            Ids(top).Should().Equal(1, 2);

            var (_, _, skip) = await Run(harness, "/orders", "?$skip=1", user: UserA);
            Ids(skip).Should().Equal(2, 3);
        }

        // ---- $count: soft-deleted + cross-tenant rows are excluded from the total -----------

        [Fact]
        public async Task Count_reports_only_live_scoped_rows()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-count", Metadata, Seed);

            var (status, _, body) = await Run(harness, "/orders", "?$count=true", user: UserA);

            status.Should().Be(200);
            // 3 live tenant-a rows — NOT 4 (ghost excluded), NOT 6 (tenant-b excluded).
            JsonDocument.Parse(body).RootElement.GetProperty("@odata.count").GetInt64().Should().Be(3);
        }

        // ---- $expand: the child expansion is independently soft-delete + tenant scoped ------

        [Fact]
        public async Task Expand_child_collection_excludes_soft_deleted_lines_and_scoped_parents()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("matrix-expand", Metadata, Seed);
            var nav = await LinesNav(harness);

            var (status, _, body) = await Run(harness, "/orders", $"?$expand={nav}&$orderby=id", user: UserA);

            status.Should().Be(200);
            // Parents: live tenant-a only (ghost 9 absent).
            Ids(body).Should().Equal(1, 2, 3);

            // Order 1's lines: the two live ones only — the soft-deleted line 7 never surfaces.
            var order1 = JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Single(r => r.GetProperty("id").GetInt32() == 1);
            order1.GetProperty(nav).EnumerateArray().Select(l => l.GetProperty("sku").GetString())
                .Should().BeEquivalentTo("sku-a1", "sku-a2");
        }

        // ---- helpers ------------------------------------------------------------------------

        private static async Task<string> LinesNav(ODataRealDbHarness harness)
        {
            var model = await harness.ModelAsync();
            var orders = model.GetTableFromDbName("orders");
            return orders.MultiLinks.Single(kv =>
                string.Equals(kv.Value.ChildTable.DbName, "order_lines", StringComparison.OrdinalIgnoreCase)).Key;
        }

        private static IReadOnlyList<int> Ids(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(r => r.GetProperty("id").GetInt32()).ToList();

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataRealDbHarness harness, string path, string queryString = "", ClaimsPrincipal? user = null)
        {
            var opts = new ODataOptions
            {
                Endpoint = ODataRealDbHarness.EndpointPath,
                ContinuationTokenSecret = "test-odata-matrix-secret-0001",
            };
            var authenticator = new ODataAuthenticator(BifrostQL.Server.BifrostAuthContextFactory.Instance, null);
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
    }
}

using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// One-level <c>$expand</c> end to end through the OData middleware over a real transformer-
    /// pipeline executor on seeded SQLite, plus pure unit coverage of the expand syntax parser.
    /// Proves: to-one AND to-many navigations return OData-shaped NESTED results executed as
    /// independent scoped intents through <see cref="Core.Resolvers.IQueryIntentExecutor"/>; the
    /// expanded (child) entity receives the SAME tenant/soft-delete/policy scope as the root, so a
    /// hidden child never appears inside a visible parent (criterion 2); a null relationship renders
    /// as null / empty array; and unknown / multi-level / nested-option / self-referential /
    /// composite-key / over-fanout expands all fail as deterministic OData 400s (criteria 3-5).
    ///
    /// <para>Composite-FK DECISION: a composite (multi-column) FK relationship is REJECTED with a
    /// clean 400, never silently bound on its first column pair. The intent executor's single-link
    /// flatten explicitly excludes composite joins, so serving one would require the adapter to
    /// hand-build a partial predicate — exactly the `column[0]` guess the composite-PK rule forbids.
    /// The explicit "unsupported relationship" 400 is the honest, fail-closed choice.</para>
    /// </summary>
    public sealed class ODataExpandTests
    {
        // Customers 1-1 (to-one) / 1-many (to-many) relational fixture with tenant scoping on BOTH
        // sides, plus deliberately planted cross-tenant edges (a tenant-a order pointing at a
        // tenant-b customer, and a tenant-b order pointing at a tenant-a customer) to prove the
        // child is scoped independently.
        private static readonly string[] RelSeed =
        {
            "CREATE TABLE Customers (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL);",
            "INSERT INTO Customers(id, tenant_id, name) VALUES " +
                "(1,'tenant-a','alice'),(2,'tenant-a','bob'),(3,'tenant-b','carol'),(4,'tenant-a','dave');",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, customer_id INTEGER, tenant_id TEXT NOT NULL, amount INTEGER NOT NULL, " +
                "FOREIGN KEY (customer_id) REFERENCES Customers(id));",
            "INSERT INTO Orders(id, customer_id, tenant_id, amount) VALUES " +
                "(10,1,'tenant-a',100),(11,1,'tenant-a',250),(12,2,'tenant-a',400)," +
                "(13,NULL,'tenant-a',50),"     + // no customer → to-one expands to null
                "(14,3,'tenant-b',900),"       + // tenant-b root, invisible to tenant-a
                "(15,3,'tenant-a',70),"        + // tenant-a root pointing at a tenant-b customer → null expand
                "(16,1,'tenant-b',999);",        // tenant-b order pointing at alice (tenant-a) → excluded from her to-many
        };

        private static readonly string[] RelMetadata =
        {
            "main.Customers { tenant-filter: tenant_id }",
            "main.Orders { tenant-filter: tenant_id }",
        };

        // Structural-rejection fixture: a self-referential FK (Nodes) and a composite FK
        // (Cities → Regions on country+code). No tenant/policy — these requests fail at planning,
        // before any row is read.
        private static readonly string[] StructSeed =
        {
            "CREATE TABLE Nodes (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT, FOREIGN KEY (parent_id) REFERENCES Nodes(id));",
            "INSERT INTO Nodes(id, parent_id, name) VALUES (1,NULL,'root'),(2,1,'child');",
            "CREATE TABLE Regions (country TEXT NOT NULL, code TEXT NOT NULL, name TEXT NOT NULL, PRIMARY KEY (country, code));",
            "INSERT INTO Regions(country, code, name) VALUES ('US','CA','California');",
            "CREATE TABLE Cities (id INTEGER PRIMARY KEY, country TEXT, region_code TEXT, name TEXT, " +
                "FOREIGN KEY (country, region_code) REFERENCES Regions(country, code));",
            "INSERT INTO Cities(id, country, region_code, name) VALUES (1,'US','CA','San Diego');",
        };

        private static readonly string[] NoMetadata = System.Array.Empty<string>();

        private static ODataOptions Opts(int maxFanout = 1000) => new()
        {
            Endpoint = ODataRealDbHarness.EndpointPath,
            ContinuationTokenSecret = "test-odata-expand-secret-0001",
            MaxExpandFanout = maxFanout,
        };

        // ---- pure parser coverage -----------------------------------------------------------

        [Fact]
        public void Parse_returns_empty_for_absent_expand()
        {
            ODataExpand.Parse(null).Should().BeEmpty();
            ODataExpand.Parse("   ").Should().BeEmpty();
        }

        [Fact]
        public void Parse_splits_comma_separated_navigations()
        {
            ODataExpand.Parse("customer, orders").Should().Equal("customer", "orders");
        }

        [Theory]
        [InlineData("customer($select=name)")]   // nested option — unsupported this slice
        [InlineData("customer(orders)")]
        [InlineData("customer/orders")]           // multi-level path — a second level
        [InlineData("a,,b")]                       // empty item
        [InlineData("customer,customer")]          // duplicate navigation
        public void Parse_rejects_unsupported_shapes(string expand)
        {
            var act = () => ODataExpand.Parse(expand);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // ---- to-one (many-to-one) expand ----------------------------------------------------

        [Fact]
        public async Task To_one_expand_nests_the_related_entity_and_nulls_absent_or_hidden_ones()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-to-one", RelMetadata, RelSeed);
            var nav = await ToOneNav(harness);
            var userA = ODataTestAuth.Principal("user-a", tenant: "tenant-a");

            var (status, _, body) = await Run(harness, "/orders", $"?$expand={nav}&$orderby=id", user: userA);

            status.Should().Be(200);
            var rows = JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().ToList();
            // tenant-a sees orders 10,11,12,13,15 (14 and 16 are tenant-b) in id order.
            rows.Select(r => r.GetProperty("id").GetInt32()).Should().Equal(10, 11, 12, 13, 15);

            // Order 10 → alice, nested as an OData object (not a flat column).
            var order10 = rows.Single(r => r.GetProperty("id").GetInt32() == 10);
            order10.GetProperty(nav).GetProperty("name").GetString().Should().Be("alice");

            // Order 13 has a NULL FK → the to-one expands to JSON null, not an error.
            var order13 = rows.Single(r => r.GetProperty("id").GetInt32() == 13);
            order13.GetProperty(nav).ValueKind.Should().Be(JsonValueKind.Null);

            // Order 15 (tenant-a) points at customer 3 (tenant-b) — hidden from tenant-a. The child
            // is scoped independently, so the expand is null: a hidden child never leaks through a
            // visible parent (criterion 2).
            var order15 = rows.Single(r => r.GetProperty("id").GetInt32() == 15);
            order15.GetProperty(nav).ValueKind.Should().Be(JsonValueKind.Null);
        }

        // ---- to-many (one-to-many) expand ---------------------------------------------------

        [Fact]
        public async Task To_many_expand_nests_a_collection_empty_when_none_and_tenant_scoped()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-to-many", RelMetadata, RelSeed);
            var nav = await ToManyNav(harness);
            var userA = ODataTestAuth.Principal("user-a", tenant: "tenant-a");

            var (status, _, body) = await Run(harness, "/customers", $"?$expand={nav}&$orderby=id", user: userA);

            status.Should().Be(200);
            var rows = JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray().ToList();
            // tenant-a sees customers 1,2,4 (carol is tenant-b).
            rows.Select(r => r.GetProperty("id").GetInt32()).Should().Equal(1, 2, 4);

            // alice's orders are the tenant-a ones only: 10 and 11. Order 16 references alice but is
            // a tenant-b row — it must NOT appear inside her collection (criterion 2 + cross-tenant).
            var alice = rows.Single(r => r.GetProperty("id").GetInt32() == 1);
            AmountsOf(alice, nav).Should().BeEquivalentTo(new[] { 100, 250 });

            // dave has no orders → an empty array, not null and not an error.
            var dave = rows.Single(r => r.GetProperty("id").GetInt32() == 4);
            var daveOrders = dave.GetProperty(nav);
            daveOrders.ValueKind.Should().Be(JsonValueKind.Array);
            daveOrders.GetArrayLength().Should().Be(0);
        }

        // ---- depth / fanout / unknown / cyclic / composite rejections -----------------------

        [Fact]
        public async Task Multi_level_and_nested_option_expand_are_clean_400s()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-depth", RelMetadata, RelSeed);
            var nav = await ToOneNav(harness);
            var userA = ODataTestAuth.Principal("user-a", tenant: "tenant-a");

            var (s1, _, b1) = await Run(harness, "/orders", $"?$expand={nav}/{nav}", user: userA);
            s1.Should().Be(400);
            ErrorCode(b1).Should().Be("BadRequest");

            var (s2, _, b2) = await Run(harness, "/orders", $"?$expand={nav}($select=name)", user: userA);
            s2.Should().Be(400);
            ErrorCode(b2).Should().Be("BadRequest");
        }

        [Fact]
        public async Task Unknown_navigation_expand_is_a_clean_400()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-unknown", RelMetadata, RelSeed);
            var userA = ODataTestAuth.Principal("user-a", tenant: "tenant-a");

            var (status, _, body) = await Run(harness, "/orders", "?$expand=not_a_navigation", user: userA);

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Fact]
        public async Task Self_referential_expand_is_rejected_as_cyclic()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-cyclic", NoMetadata, StructSeed);
            var model = await harness.ModelAsync();
            var nodes = model.GetTableFromDbName("Nodes");
            // The to-one self link (Nodes → Nodes) — its target entity is the source entity.
            var selfNav = nodes.SingleLinks.Single(kv =>
                string.Equals(kv.Value.ParentTable.DbName, "Nodes", System.StringComparison.OrdinalIgnoreCase)).Key;

            var (status, _, body) = await Run(harness, "/nodes", $"?$expand={selfNav}");

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Fact]
        public async Task Composite_fk_expand_is_rejected_not_silently_first_column_mapped()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-composite", NoMetadata, StructSeed);
            var model = await harness.ModelAsync();
            var cities = model.GetTableFromDbName("Cities");
            var compositeNav = cities.SingleLinks.Single(kv =>
                string.Equals(kv.Value.ParentTable.DbName, "Regions", System.StringComparison.OrdinalIgnoreCase));
            compositeNav.Value.IsComposite.Should().BeTrue("the fixture's FK spans country+code");

            var (status, _, body) = await Run(harness, "/cities", $"?$expand={compositeNav.Key}");

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        [Fact]
        public async Task Over_fanout_expand_is_a_clean_400()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("expand-fanout", RelMetadata, RelSeed);
            var nav = await ToManyNav(harness);
            var userA = ODataTestAuth.Principal("user-a", tenant: "tenant-a");

            // tenant-a's customers own 3 orders (10,11,12); a fan-out ceiling of 1 is exceeded → 400,
            // and the child intent was capped at ceiling+1 so the breach is detected without
            // materializing the whole collection (invariant 6).
            var (status, _, body) = await Run(harness, "/customers", $"?$expand={nav}", user: userA, options: Opts(maxFanout: 1));

            status.Should().Be(400);
            ErrorCode(body).Should().Be("BadRequest");
        }

        // ---- helpers ------------------------------------------------------------------------

        private static async Task<string> ToOneNav(ODataRealDbHarness harness)
        {
            var model = await harness.ModelAsync();
            var orders = model.GetTableFromDbName("Orders");
            return orders.SingleLinks.Single(kv =>
                string.Equals(kv.Value.ParentTable.DbName, "Customers", System.StringComparison.OrdinalIgnoreCase)).Key;
        }

        private static async Task<string> ToManyNav(ODataRealDbHarness harness)
        {
            var model = await harness.ModelAsync();
            var customers = model.GetTableFromDbName("Customers");
            return customers.MultiLinks.Single(kv =>
                string.Equals(kv.Value.ChildTable.DbName, "Orders", System.StringComparison.OrdinalIgnoreCase)).Key;
        }

        private static IReadOnlyList<int> AmountsOf(JsonElement parent, string nav)
            => parent.GetProperty(nav).EnumerateArray().Select(o => o.GetProperty("amount").GetInt32()).ToList();

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
            var bodyText = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, bodyText);
        }

        private static string? ErrorCode(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}

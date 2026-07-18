using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Captured, reproducible Excel and Power BI OData request shapes (slice-7 acceptance
    /// criterion 4). Both clients drive the Microsoft "Mashup"/Power Query engine, so their wire
    /// shape is well-known and pinned here as literal path + query + header sets: Power BI probes
    /// <c>$metadata</c> then previews rows with <c>$top</c>; Excel pulls the service document then
    /// GETs the entity set — each carrying its real <c>OData-MaxVersion</c>/<c>Accept</c>/
    /// <c>User-Agent</c> headers.
    ///
    /// <para><b>The load-bearing assertion is the ABSENCE of a bypass.</b> The vendor-shaped
    /// request travels the IDENTICAL <see cref="ODataMiddleware"/> path as any other: it is
    /// auth-gated (an unauthenticated vendor-shaped request is a 401 despite its User-Agent) and
    /// tenant/soft-delete scoped (two tenants issuing byte-identical requests see disjoint rows;
    /// a soft-deleted row never surfaces). No User-Agent, Accept, or OData-version header is a
    /// fast-path that skips authentication or the transformer pipeline — a product-specific
    /// bypass would be a security regression, so its absence is asserted directly.</para>
    /// </summary>
    public sealed class ODataVendorClientShapeTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, deleted_at TEXT);",
            "INSERT INTO orders(id, tenant_id, name, deleted_at) VALUES " +
                "(1,'tenant-a','a-one',NULL)," +
                "(2,'tenant-a','a-two',NULL)," +
                "(9,'tenant-a','a-ghost','2026-01-01T00:00:00Z')," +  // soft-deleted
                "(3,'tenant-b','b-one',NULL);",
        };

        private static readonly string[] Metadata =
        {
            "main.orders { tenant-filter: tenant_id; soft-delete: deleted_at }",
        };

        // Power BI Desktop's OData.Feed connector: Mashup engine User-Agent + OData v4 negotiation.
        private static readonly IReadOnlyDictionary<string, string> PowerBiHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = "Microsoft.Data.Mashup (https://go.microsoft.com/fwlink/?LinkID=304225)",
            ["OData-MaxVersion"] = "4.0",
            ["OData-Version"] = "4.0",
            ["Accept"] = "application/json;odata.metadata=minimal;q=1.0, application/json;q=0.9, */*;q=0.8",
        };

        // Excel's "From OData Feed" (Power Query in Excel) rides the same Mashup engine.
        private static readonly IReadOnlyDictionary<string, string> ExcelHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = "Microsoft.Data.Mashup (Microsoft Excel)",
            ["OData-MaxVersion"] = "4.0",
            ["Accept"] = "application/json;odata.metadata=minimal",
        };

        private static ClaimsPrincipal UserA => ODataTestAuth.Principal("user-a", tenant: "tenant-a");
        private static ClaimsPrincipal UserB => ODataTestAuth.Principal("user-b", tenant: "tenant-b");

        // ---- Power BI ------------------------------------------------------------------------

        [Fact]
        public async Task PowerBi_metadata_probe_is_auth_gated_and_identity_filtered()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("vendor-pbi-meta", Metadata, Seed);

            // Authenticated: the vendor $metadata probe returns the CSDL for the readable set.
            var (status, contentType, body) = await Run(harness, "/$metadata", user: UserA, headers: PowerBiHeaders);
            status.Should().Be(200);
            contentType.Should().Contain("application/xml");
            XDocument.Parse(body).Root!.Name.LocalName.Should().Be("Edmx");

            // Unauthenticated but carrying the exact Power BI headers → still 401. The User-Agent is
            // not a bypass.
            var (anonStatus, _, _) = await Run(harness, "/$metadata", user: null, headers: PowerBiHeaders);
            anonStatus.Should().Be(401);
        }

        [Fact]
        public async Task PowerBi_top_preview_is_tenant_and_soft_delete_scoped()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("vendor-pbi-top", Metadata, Seed);

            // Power BI's row preview: GET orders?$top=1000. Same request, two tenants → disjoint rows.
            var (statusA, _, bodyA) = await Run(harness, "/orders", "?$top=1000", user: UserA, headers: PowerBiHeaders);
            statusA.Should().Be(200);
            Names(bodyA).Should().BeEquivalentTo("a-one", "a-two");   // no tenant-b, no soft-deleted ghost

            var (_, _, bodyB) = await Run(harness, "/orders", "?$top=1000", user: UserB, headers: PowerBiHeaders);
            Names(bodyB).Should().BeEquivalentTo("b-one");
        }

        // ---- Excel ---------------------------------------------------------------------------

        [Fact]
        public async Task Excel_service_document_is_auth_gated_and_identity_filtered()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("vendor-xl-doc", Metadata, Seed);

            var (status, contentType, body) = await Run(harness, "/", user: UserA, headers: ExcelHeaders);
            status.Should().Be(200);
            contentType.Should().Contain("application/json");
            JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(v => v.GetProperty("name").GetString()).Should().Contain("orders");

            var (anonStatus, _, _) = await Run(harness, "/", user: null, headers: ExcelHeaders);
            anonStatus.Should().Be(401);
        }

        [Fact]
        public async Task Excel_entity_get_is_tenant_and_soft_delete_scoped()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("vendor-xl-get", Metadata, Seed);

            var (statusA, _, bodyA) = await Run(harness, "/orders", user: UserA, headers: ExcelHeaders);
            statusA.Should().Be(200);
            Names(bodyA).Should().BeEquivalentTo("a-one", "a-two");   // scoped + soft-delete filtered

            var (_, _, bodyB) = await Run(harness, "/orders", user: UserB, headers: ExcelHeaders);
            Names(bodyB).Should().BeEquivalentTo("b-one");
        }

        // ---- helpers ------------------------------------------------------------------------

        private static IReadOnlyList<string?> Names(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(r => r.GetProperty("name").GetString()).ToList();

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataRealDbHarness harness, string path, string queryString = "",
            ClaimsPrincipal? user = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            var opts = new ODataOptions
            {
                Endpoint = ODataRealDbHarness.EndpointPath,
                ContinuationTokenSecret = "test-odata-vendor-secret-0001",
            };
            var authenticator = new ODataAuthenticator(BifrostQL.Server.BifrostAuthContextFactory.Instance, null);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            var middleware = new ODataMiddleware(
                next, opts, authenticator, harness.Reads, NullLogger<ODataMiddleware>.Instance);

            // A null user models a truly unauthenticated request (no principal on HttpContext.User).
            var ctx = new DefaultHttpContext();
            if (user is not null)
                ctx.User = user;
            ctx.Request.Path = path;
            if (queryString.Length > 0)
                ctx.Request.QueryString = new QueryString(queryString.StartsWith('?') ? queryString : "?" + queryString);
            if (headers is not null)
                foreach (var (key, value) in headers)
                    ctx.Request.Headers[key] = value;
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, body);
        }
    }
}

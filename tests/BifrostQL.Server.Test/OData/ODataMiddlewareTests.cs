using System.Text.Json;
using System.Xml.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Middleware-level behavior: the endpoint owns only HTTP/OData codec — it authenticates via
    /// the injected authenticator, serves the two discovery documents (service document as OData
    /// JSON, <c>$metadata</c> as CSDL XML) filtered to the caller's readable tables, answers any
    /// not-yet-implemented data path with a clean 501, maps every failure to a deterministic OData
    /// JSON error envelope, and never leaks internal or credential detail onto the wire.
    /// </summary>
    public sealed class ODataMiddlewareTests
    {
        private static ODataMiddleware Build(
            IODataBasicCredentialStore? store = null,
            ODataOptions? options = null,
            IQueryIntentExecutor? reads = null)
        {
            var opts = options ?? new ODataOptions();
            var authenticator = new ODataAuthenticator(BifrostAuthContextFactory.Instance, store);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            return new ODataMiddleware(next, opts, authenticator, reads ?? StubReads(), NullLogger<ODataMiddleware>.Instance);
        }

        /// <summary>A reads stub whose model is never reached on the auth-negative paths.</summary>
        private static IQueryIntentExecutor StubReads()
        {
            var reads = Substitute.For<IQueryIntentExecutor>();
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(Array.Empty<IDbTable>());
            reads.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            return reads;
        }

        private static async Task<(int Status, string ContentType, string Body)> Run(
            ODataMiddleware middleware, HttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, body);
        }

        private static JsonElement ErrorOf(string body)
            => JsonDocument.Parse(body).RootElement.GetProperty("error");

        // ---- auth-negative contract (data path never reached) ---------------------------------

        [Fact]
        public async Task Anonymous_request_returns_401_with_challenge_and_json_envelope()
        {
            var options = new ODataOptions { Realm = "TestRealm" };
            var ctx = new DefaultHttpContext();

            var (status, contentType, body) = await Run(Build(options: options), ctx);

            status.Should().Be(401);
            ErrorOf(body).GetProperty("code").GetString().Should().Be("Unauthorized");
            contentType.Should().Contain("json");
            ctx.Response.Headers.WWWAuthenticate.ToString().Should().Contain("Basic realm=\"TestRealm\"");
        }

        [Fact]
        public async Task Bad_basic_credentials_return_401_without_leaking_the_secret()
        {
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal());
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, "wrong-password");

            var (status, _, body) = await Run(Build(store), ctx);

            status.Should().Be(401);
            body.Should().NotContain(ODataTestAuth.Password);
            body.Should().NotContain("wrong-password");
        }

        [Fact]
        public async Task Non_discovery_path_returns_not_implemented_501()
        {
            // Entity reads/query options are later slices; an authenticated request to a data path
            // (not the service document or $metadata) is a clean 501.
            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal() };
            ctx.Request.Path = "/Orders";

            var (status, _, body) = await Run(Build(), ctx);

            status.Should().Be(501);
            ErrorOf(body).GetProperty("code").GetString().Should().Be("NotImplemented");
        }

        [Fact]
        public async Task Cancelled_request_writes_no_body()
        {
            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal() };
            ctx.RequestAborted = new CancellationToken(canceled: true);
            ctx.Response.Body = new MemoryStream();

            await Build().InvokeAsync(ctx);

            ctx.Response.Body.Length.Should().Be(0, "a client abort short-circuits before any write");
        }

        // ---- discovery documents (real model, identity-filtered) ------------------------------

        [Fact]
        public async Task Service_document_returns_odata_json_listing_readable_entity_sets_only()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-doc", Metadata, Seed);
            var options = new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath };

            // Authenticated, non-admin caller (no admin role) hitting the endpoint root.
            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal() };
            ctx.Request.Path = "/";

            var (status, contentType, body) = await Run(Build(options: options, reads: harness.Reads), ctx);

            status.Should().Be(200);
            contentType.Should().Contain("application/json");
            var value = JsonDocument.Parse(body).RootElement.GetProperty("value").EnumerateArray()
                .Select(v => v.GetProperty("name").GetString()).ToList();
            // EntitySet names are the tables' schema-derived GraphQL names (camelCase).
            value.Should().Contain(new[] { "orders", "orderLines", "tags" });
            value.Should().NotContain("customers");  // create-only → not readable by a non-admin
            value.Should().NotContain("auditLog");   // create-only → not readable by a non-admin
        }

        [Fact]
        public async Task Metadata_returns_csdl_xml_document()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("mw-meta", Metadata, Seed);
            var options = new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath };

            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal() };
            ctx.Request.Path = "/$metadata";

            var (status, contentType, body) = await Run(Build(options: options, reads: harness.Reads), ctx);

            status.Should().Be(200);
            contentType.Should().Contain("application/xml");
            var root = XDocument.Parse(body).Root!;
            root.Name.LocalName.Should().Be("Edmx");
            ((string?)root.Attribute("Version")).Should().Be("4.0");
        }

        private static readonly string[] Seed =
        {
            "CREATE TABLE Customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES Customers(id), total REAL, created_at DATETIME);",
            "CREATE TABLE OrderLines (order_id INTEGER NOT NULL REFERENCES Orders(id), line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no));",
            "CREATE TABLE Tags (id INTEGER PRIMARY KEY, label TEXT NOT NULL);",
            "CREATE TABLE AuditLog (id INTEGER PRIMARY KEY, message TEXT NOT NULL);",
        };

        private static readonly string[] Metadata =
        {
            "main.Customers { policy-actions: create }",
            "main.Orders { policy-actions: read }",
            "main.AuditLog { policy-actions: create }",
        };
    }
}

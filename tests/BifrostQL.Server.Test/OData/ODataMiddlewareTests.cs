using System.Text.Json;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Middleware-level behavior: the endpoint owns only HTTP/OData codec — it authenticates via
    /// the injected authenticator, answers a not-yet-implemented data path with a clean 501, maps
    /// every failure to a deterministic OData JSON error envelope, and never leaks internal or
    /// credential detail onto the wire.
    /// </summary>
    public sealed class ODataMiddlewareTests
    {
        private static ODataMiddleware Build(IODataBasicCredentialStore? store = null, ODataOptions? options = null)
        {
            var opts = options ?? new ODataOptions();
            var authenticator = new ODataAuthenticator(BifrostAuthContextFactory.Instance, store);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            return new ODataMiddleware(next, opts, authenticator, NullLogger<ODataMiddleware>.Instance);
        }

        private static async Task<(int Status, string Body, JsonElement Error)> Run(
            ODataMiddleware middleware, HttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
            return (ctx.Response.StatusCode, body, error);
        }

        [Fact]
        public async Task Authenticated_request_returns_not_implemented_501()
        {
            // Slice 1 has no data/metadata path; an authenticated request is a clean 501.
            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal() };

            var (status, _, error) = await Run(Build(), ctx);

            status.Should().Be(501);
            error.GetProperty("code").GetString().Should().Be("NotImplemented");
        }

        [Fact]
        public async Task Anonymous_request_returns_401_with_challenge_and_json_envelope()
        {
            var options = new ODataOptions { Realm = "TestRealm" };
            var ctx = new DefaultHttpContext();

            var (status, _, error) = await Run(Build(options: options), ctx);

            status.Should().Be(401);
            error.GetProperty("code").GetString().Should().Be("Unauthorized");
            ctx.Response.ContentType.Should().Contain("json");
            ctx.Response.Headers.WWWAuthenticate.ToString().Should().Contain("Basic realm=\"TestRealm\"");
        }

        [Fact]
        public async Task Bad_basic_credentials_return_401_without_leaking_the_secret()
        {
            var store = new FakeODataBasicCredentialStore().Add(
                ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal());
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, "wrong-password");

            var (status, body, _) = await Run(Build(store), ctx);

            status.Should().Be(401);
            // The wire must never carry the stored secret or the presented password.
            body.Should().NotContain(ODataTestAuth.Password);
            body.Should().NotContain("wrong-password");
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
    }
}

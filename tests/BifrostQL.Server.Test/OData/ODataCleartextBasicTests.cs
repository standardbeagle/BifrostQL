using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// New-front-door checklist: "Refuse credentials on cleartext transport before reading
    /// them." OData Basic used to decode, rate-limit and verify a password that had just crossed
    /// plain HTTP. A Basic request whose <c>Request.IsHttps</c> is false is now refused with
    /// 403 HttpsRequired before the store is asked, unless the default-off
    /// <see cref="ODataOptions.AllowCleartextBasicAuth"/> is set (which logs a warning).
    /// </summary>
    public sealed class ODataCleartextBasicTests
    {
        private static ODataMiddleware Middleware(ODataAuthenticator authenticator, ODataOptions options)
        {
            var reads = Substitute.For<IQueryIntentExecutor>();
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(Array.Empty<IDbTable>());
            reads.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            return new ODataMiddleware(next, options, authenticator, reads, NullLogger<ODataMiddleware>.Instance);
        }

        private static async Task<(int Status, string Body)> Run(ODataMiddleware middleware, HttpContext ctx)
        {
            ctx.Request.Method = "GET";
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
        }

        private static DefaultHttpContext BasicRequest(bool https, string password = ODataTestAuth.Password)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.IsHttps = https;
            ctx.Request.Headers.Authorization = ODataTestAuth.BasicHeader(ODataTestAuth.Username, password);
            return ctx;
        }

        [Theory]
        [InlineData(ODataTestAuth.Password)]
        [InlineData("wrong-password")]
        public async Task Basic_on_cleartext_is_refused_before_the_store_is_asked(string password)
        {
            var store = new CountingStore();
            var options = new ODataOptions();
            var auth = new ODataAuthenticator(BifrostAuthContextFactory.Instance, store, options: options);

            var (status, body) = await Run(Middleware(auth, options), BasicRequest(https: false, password));

            status.Should().Be(403);
            var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("HttpsRequired");
            error.GetProperty("message").GetString().Should().Be("Basic credentials are accepted only over HTTPS.");
            store.Lookups.Should().Be(0, "the credential must be refused before it is read or looked up");
        }

        [Fact]
        public async Task Basic_on_https_is_accepted()
        {
            var store = new CountingStore();
            var auth = new ODataAuthenticator(BifrostAuthContextFactory.Instance, store);

            var userContext = await auth.AuthenticateAsync(BasicRequest(https: true), CancellationToken.None);

            userContext.Should().NotBeEmpty();
            store.Lookups.Should().Be(1);
        }

        [Fact]
        public async Task Cleartext_opt_out_accepts_Basic_and_warns_at_construction()
        {
            var store = new CountingStore();
            var logger = new FakeLogger<ODataAuthenticator>();
            var auth = new ODataAuthenticator(BifrostAuthContextFactory.Instance, store, logger,
                options: new ODataOptions { AllowCleartextBasicAuth = true });

            logger.Collector.GetSnapshot().Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("AllowCleartextBasicAuth is ON"));
            var userContext = await auth.AuthenticateAsync(BasicRequest(https: false), CancellationToken.None);
            userContext.Should().NotBeEmpty();
        }

        [Fact]
        public void Default_options_log_no_cleartext_warning()
        {
            var logger = new FakeLogger<ODataAuthenticator>();
            _ = new ODataAuthenticator(BifrostAuthContextFactory.Instance, new CountingStore(), logger);

            new ODataOptions().AllowCleartextBasicAuth.Should().BeFalse();
            logger.Collector.GetSnapshot().Should().NotContain(r => r.Level == LogLevel.Warning);
        }

        [Fact]
        public async Task Bearer_on_cleartext_is_not_affected()
        {
            var ctx = new DefaultHttpContext { User = ODataTestAuth.Principal("bearer-sub") };

            var userContext = await new ODataAuthenticator(BifrostAuthContextFactory.Instance, new CountingStore())
                .AuthenticateAsync(ctx, CancellationToken.None);

            userContext.Should().NotBeEmpty();
        }

        private sealed class CountingStore : IODataBasicCredentialStore
        {
            private readonly FakeODataBasicCredentialStore _inner = new FakeODataBasicCredentialStore()
                .Add(ODataTestAuth.Username, ODataTestAuth.Password, ODataTestAuth.Principal());

            public int Lookups { get; private set; }

            public Task<ODataBasicCredential?> FindAsync(string username, CancellationToken cancellationToken)
            {
                Lookups++;
                return _inner.FindAsync(username, cancellationToken);
            }
        }
    }
}

using System.Security.Claims;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// RED/GREEN TDD coverage for <see cref="BifrostAppMetadataMiddleware"/>,
    /// which exposes the app-metadata overlay to SPA / React Native clients as
    /// the stable camelCase JSON contract. The middleware mirrors the established
    /// <see cref="BifrostInfoMiddleware"/> pattern: a GET endpoint at a
    /// configurable path that short-circuits the pipeline.
    /// </summary>
    public class BifrostAppMetadataMiddlewareTests
    {
        /// <summary>
        /// An authenticated caller. The endpoint requires auth by DEFAULT now, so the tests
        /// below — which exercise routing, shape and the stable contract rather than the gate —
        /// must carry an identity like any real client would.
        /// </summary>
        private static ClaimsPrincipal AuthenticatedUser() =>
            new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "app-user") }, authenticationType: "test"));

        private static ServiceProvider BuildServices(AppMetadataModel? overlay = null)
        {
            var services = new ServiceCollection();
            if (overlay != null)
                services.AddSingleton(new AppMetadataCache(() => Task.FromResult(overlay)));
            return services.BuildServiceProvider();
        }

        private static AppMetadataModel SampleOverlay() => new()
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.members"] = new EntityMetadata
                {
                    Label = "Members",
                    DisplayFields = new[] { "first_name" },
                },
            },
        };

        [Fact]
        public async Task Middleware_ServesOverlayJsonOnGetToConfiguredPath()
        {
            var options = new BifrostAppMetadataOptions { Path = "/_app-metadata" };
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext
            {
                RequestServices = BuildServices(SampleOverlay()),
                User = AuthenticatedUser(),
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_app-metadata";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            called.Should().BeFalse("the endpoint should short-circuit the pipeline");
            context.Response.StatusCode.Should().Be(200);
            context.Response.ContentType.Should().Be("application/json");

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
            var restored = AppMetadataJson.Deserialize(json);
            restored.Entities.Should().ContainKey("dbo.members");
            restored.Entities["dbo.members"].Label.Should().Be("Members");
        }

        [Fact]
        public async Task Middleware_ServesCamelCaseContract()
        {
            var options = new BifrostAppMetadataOptions();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext
            {
                RequestServices = BuildServices(SampleOverlay()),
                User = AuthenticatedUser(),
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_app-metadata";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
            // The contract is the same camelCase RN-friendly shape from sub-task 1.
            json.Should().Contain("\"entities\"").And.Contain("\"displayFields\"");
        }

        [Fact]
        public async Task Middleware_ServesEmptyOverlayWhenNoneRegistered()
        {
            var options = new BifrostAppMetadataOptions();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext
            {
                RequestServices = BuildServices(),
                User = AuthenticatedUser(),
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_app-metadata";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(200);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
            AppMetadataJson.Deserialize(json).Entities.Should().BeEmpty();
        }

        [Fact]
        public async Task Middleware_PassesThroughForNonMatchingPath()
        {
            var options = new BifrostAppMetadataOptions { Path = "/_app-metadata" };
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/graphql";

            await middleware.InvokeAsync(context);

            called.Should().BeTrue("non-matching paths should be passed through");
        }

        [Fact]
        public async Task Middleware_PassesThroughForPostRequests()
        {
            var options = new BifrostAppMetadataOptions { Path = "/_app-metadata" };
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/_app-metadata";

            await middleware.InvokeAsync(context);

            called.Should().BeTrue("POST requests should be passed through");
        }

        [Fact]
        public async Task Middleware_MatchesPathCaseInsensitively()
        {
            var options = new BifrostAppMetadataOptions { Path = "/_app-metadata" };
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext
            {
                RequestServices = BuildServices(),
                User = AuthenticatedUser(),
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_APP-METADATA";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            called.Should().BeFalse("case-insensitive path should match");
            context.Response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task Middleware_Returns401ByDefaultForAnAnonymousCaller()
        {
            // Default options — no RequireAuth set. The overlay is introspection, so an
            // anonymous caller gets nothing without an explicit deployment opt-in.
            var options = new BifrostAppMetadataOptions();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices(SampleOverlay()) };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_app-metadata";

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Middleware_ServesAnonymousCallersOnlyWhenAuthIsExplicitlyDisabled()
        {
            // The explicit opt-in that used to be the silent default.
            var options = new BifrostAppMetadataOptions { RequireAuth = false };
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices(SampleOverlay()) };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_app-metadata";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task Middleware_Returns401WhenAuthRequiredAndNotAuthenticated()
        {
            var options = new BifrostAppMetadataOptions { RequireAuth = true };
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostAppMetadataMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_app-metadata";

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(401);
        }

        [Fact]
        public void Options_HaveCorrectDefaults()
        {
            var options = new BifrostAppMetadataOptions();

            options.Enabled.Should().BeTrue();
            options.Path.Should().Be("/_app-metadata");
            // CHANGED DEFAULT. This assertion previously read `BeFalse()` — it encoded an
            // unauthenticated, unfiltered introspection endpoint as the intended out-of-the-box
            // behaviour. It is re-pointed, not deleted: the open endpoint is now the explicit
            // opt-in covered by Middleware_ServesAnonymousCallersOnlyWhenAuthIsExplicitlyDisabled.
            options.RequireAuth.Should().BeTrue();
        }
    }
}

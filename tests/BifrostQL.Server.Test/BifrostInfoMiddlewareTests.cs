using System.Text.Json;
using BifrostQL.Core.Modules;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class BifrostInfoMiddlewareTests
    {
        private static BifrostServerClock CreateClock() => new();

        private static ServiceProvider BuildServices(
            IMutationModules? modules = null,
            IFilterTransformers? filterTransformers = null,
            IMutationTransformers? mutationTransformers = null,
            IQueryObservers? queryObservers = null)
        {
            var services = new ServiceCollection();
            if (modules != null)
                services.AddSingleton(modules);
            if (filterTransformers != null)
                services.AddSingleton(filterTransformers);
            if (mutationTransformers != null)
                services.AddSingleton(mutationTransformers);
            if (queryObservers != null)
                services.AddSingleton(queryObservers);
            return services.BuildServiceProvider();
        }

        [Fact]
        public async Task InfoMiddleware_ReturnsJsonOnGetToConfiguredPath()
        {
            var options = new BifrostInfoOptions { Path = "/_info" };
            var clock = CreateClock();
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            called.Should().BeFalse("info endpoint should short-circuit the pipeline");
            context.Response.StatusCode.Should().Be(200);
            context.Response.ContentType.Should().Be("application/json");

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await JsonSerializer.DeserializeAsync<BifrostInfoResponse>(context.Response.Body);
            body.Should().NotBeNull();
            body!.Version.Should().NotBeNullOrEmpty();
            body.ServerStartTime.Should().Be(clock.StartTime);
        }

        [Fact]
        public async Task InfoMiddleware_PassesThroughForNonMatchingPath()
        {
            var options = new BifrostInfoOptions { Path = "/_info" };
            var clock = CreateClock();
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/graphql";

            await middleware.InvokeAsync(context, clock);

            called.Should().BeTrue("non-matching paths should be passed through");
        }

        [Fact]
        public async Task InfoMiddleware_PassesThroughForPostRequests()
        {
            var options = new BifrostInfoOptions { Path = "/_info" };
            var clock = CreateClock();
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/_info";

            await middleware.InvokeAsync(context, clock);

            called.Should().BeTrue("POST requests should be passed through");
        }

        [Fact]
        public async Task InfoMiddleware_PassesThroughWhenDisabled()
        {
            var options = new BifrostInfoOptions { Enabled = false, Path = "/_info" };
            var clock = CreateClock();
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";

            await middleware.InvokeAsync(context, clock);

            called.Should().BeTrue("disabled endpoint should pass through");
        }

        [Fact]
        public async Task InfoMiddleware_Returns401WhenAuthRequiredAndNotAuthenticated()
        {
            var options = new BifrostInfoOptions { RequireAuth = true };
            var clock = CreateClock();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";

            await middleware.InvokeAsync(context, clock);

            context.Response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task InfoMiddleware_MatchesPathCaseInsensitively()
        {
            var options = new BifrostInfoOptions { Path = "/_info" };
            var clock = CreateClock();
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_INFO";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            called.Should().BeFalse("case-insensitive path should match");
            context.Response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task InfoMiddleware_IncludesUptimeInResponse()
        {
            var options = new BifrostInfoOptions();
            var clock = CreateClock();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await JsonSerializer.DeserializeAsync<BifrostInfoResponse>(context.Response.Body);
            body!.Uptime.Should().NotBeNullOrEmpty();
            body.Uptime.Should().Contain("m");
        }

        [Fact]
        public async Task InfoMiddleware_ReportsNoneForSchemaCacheWhenNoCacheRegistered()
        {
            var options = new BifrostInfoOptions();
            var clock = CreateClock();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await JsonSerializer.DeserializeAsync<BifrostInfoResponse>(context.Response.Body);
            body!.SchemaCacheStatus.Should().Be("none");
        }

        [Fact]
        public async Task InfoMiddleware_ReportsUnknownDatabaseWhenNoOptionsRegistered()
        {
            var options = new BifrostInfoOptions();
            var clock = CreateClock();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await JsonSerializer.DeserializeAsync<BifrostInfoResponse>(context.Response.Body);
            body!.DatabaseType.Should().Be("Unknown");
        }

        [Fact]
        public async Task InfoMiddleware_ReturnsEmptyModulesListByDefault()
        {
            var options = new BifrostInfoOptions();
            var clock = CreateClock();
            RequestDelegate next = _ => Task.CompletedTask;
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_info";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await JsonSerializer.DeserializeAsync<BifrostInfoResponse>(context.Response.Body);
            body!.EnabledModules.Should().BeEmpty();
        }

        [Fact]
        public async Task InfoMiddleware_UsesCustomPath()
        {
            var options = new BifrostInfoOptions { Path = "/_status" };
            var clock = CreateClock();
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var middleware = new BifrostInfoMiddleware(next, options);

            var context = new DefaultHttpContext { RequestServices = BuildServices() };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/_status";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, clock);

            called.Should().BeFalse("custom path should be handled");
            context.Response.StatusCode.Should().Be(200);
        }
    }

    public class BifrostHeaderMiddlewareTests
    {
        [Fact]
        public async Task HeaderMiddleware_SetsServerHeader()
        {
            var called = false;
            RequestDelegate next = ctx =>
            {
                called = true;
                return Task.CompletedTask;
            };
            var middleware = new BifrostHeaderMiddleware(next);

            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            called.Should().BeTrue("next middleware should be called");
            context.Response.Headers["Server"].ToString().Should().StartWith("BifrostQL/");
        }

        [Fact]
        public void GetVersion_ReturnsNonEmptyString()
        {
            var version = BifrostHeaderMiddleware.GetVersion();
            version.Should().NotBeNullOrEmpty();
        }
    }

    public class BifrostInfoOptionsTests
    {
        [Fact]
        public void DefaultOptions_HasCorrectDefaults()
        {
            var options = new BifrostInfoOptions();

            options.Enabled.Should().BeTrue();
            options.Path.Should().Be("/_info");
            options.RequireAuth.Should().BeFalse();
            options.IncludeSchemaHash.Should().BeTrue();
        }

        [Fact]
        public void InfoResponse_SerializesCorrectly()
        {
            var response = new BifrostInfoResponse
            {
                Version = "1.0.0",
                DatabaseType = "SqlServer",
                Uptime = "5m 30s",
                SchemaCacheStatus = "active",
                EnabledModules = new[] { "BasicAuditModule" },
                SchemaHash = "abc123",
                ServerStartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };

            var json = JsonSerializer.Serialize(response);
            var deserialized = JsonSerializer.Deserialize<BifrostInfoResponse>(json);

            deserialized.Should().NotBeNull();
            deserialized!.Version.Should().Be("1.0.0");
            deserialized.DatabaseType.Should().Be("SqlServer");
            deserialized.Uptime.Should().Be("5m 30s");
            deserialized.SchemaCacheStatus.Should().Be("active");
            deserialized.EnabledModules.Should().ContainSingle().Which.Should().Be("BasicAuditModule");
            deserialized.SchemaHash.Should().Be("abc123");
            deserialized.ServerStartTime.Should().Be(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        [Fact]
        public void InfoResponse_SerializesWithCorrectJsonPropertyNames()
        {
            var response = new BifrostInfoResponse
            {
                Version = "1.0.0",
                DatabaseType = "SqlServer",
            };

            var json = JsonSerializer.Serialize(response);

            json.Should().Contain("\"version\"");
            json.Should().Contain("\"databaseType\"");
            json.Should().Contain("\"uptime\"");
            json.Should().Contain("\"schemaCacheStatus\"");
            json.Should().Contain("\"enabledModules\"");
            json.Should().Contain("\"serverStartTime\"");
        }
    }
}

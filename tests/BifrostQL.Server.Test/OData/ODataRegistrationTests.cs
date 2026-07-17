using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Startup/configuration contract: the OData endpoint is opt-in and disabled by default,
    /// its DI seams resolve (with and without a Basic store), and mounting it never alters the
    /// existing routes — a disabled endpoint maps nothing, an enabled one handles only its own
    /// prefix and lets every other path fall through untouched.
    /// </summary>
    public sealed class ODataRegistrationTests
    {
        [Fact]
        public void Options_default_to_disabled_on_the_conventional_prefix()
        {
            var options = new ODataOptions();

            options.Enabled.Should().BeFalse("the endpoint is opt-in");
            options.RoutePrefix.Should().Be("/odata");
        }

        [Fact]
        public void RegisterODataServices_wires_options_and_authenticator_without_a_basic_store()
        {
            var provider = BuildProvider(new ODataOptions { Enabled = true }, withBasicStore: false);

            provider.GetService<ODataOptions>().Should().NotBeNull();
            provider.GetService<ODataAuthenticator>().Should().NotBeNull(
                "the authenticator resolves even when no Basic store is registered (Bearer-only)");
        }

        [Fact]
        public void RegisterODataServices_wires_authenticator_with_a_basic_store()
        {
            var provider = BuildProvider(new ODataOptions { Enabled = true }, withBasicStore: true);

            provider.GetService<ODataAuthenticator>().Should().NotBeNull();
        }

        [Fact]
        public async Task Disabled_endpoint_maps_nothing()
        {
            var pipeline = BuildPipeline(new ODataOptions { Enabled = false });

            var ctx = RequestFor("/odata");
            await pipeline(ctx);

            ctx.Response.StatusCode.Should().Be(200, "a disabled endpoint mounts no middleware; the request falls through");
        }

        [Fact]
        public async Task Enabled_endpoint_handles_its_prefix_and_leaves_other_routes_untouched()
        {
            var pipeline = BuildPipeline(new ODataOptions { Enabled = true });

            // A request to the OData prefix is gated by the middleware (anonymous → 401),
            // never reaching the 200 fallback.
            var odataCtx = RequestFor("/odata");
            await pipeline(odataCtx);
            odataCtx.Response.StatusCode.Should().Be(401);

            // A request to any other route is untouched — the fallback answers 200.
            var otherCtx = RequestFor("/graphql");
            await pipeline(otherCtx);
            otherCtx.Response.StatusCode.Should().Be(200);
        }

        private static ServiceProvider BuildProvider(ODataOptions options, bool withBasicStore)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IBifrostAuthContextFactory>(BifrostAuthContextFactory.Instance);
            if (withBasicStore)
                services.AddSingleton<IODataBasicCredentialStore>(new FakeODataBasicCredentialStore());
            BifrostServiceRegistrar.RegisterODataServices(services, options);
            return services.BuildServiceProvider();
        }

        private static RequestDelegate BuildPipeline(ODataOptions options)
        {
            var provider = BuildProvider(options, withBasicStore: false);
            var app = new ApplicationBuilder(provider);
            app.UseBifrostOData();
            app.Run(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });
            return app.Build();
        }

        private static DefaultHttpContext RequestFor(string path)
        {
            var ctx = new DefaultHttpContext { Request = { Path = path } };
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }
    }
}

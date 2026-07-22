using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Feeds;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Feeds
{
    /// <summary>
    /// The opt-in registration + mounting contract: <c>AddBifrostFeeds</c> registers the feed seams and
    /// <c>UseBifrostFeeds</c> mounts a dedicated <c>/feeds</c> branch — but only when enabled. A host that
    /// never registers the endpoint gets an inert <c>UseBifrostFeeds</c> that alters no route.
    /// </summary>
    public sealed class FeedRegistrationTests
    {
        private static readonly FeedOptions Feed = new()
        {
            Title = "T",
            Link = "https://example.test/feed",
            Author = "Op",
        };

        private static IBifrostAuthContextFactory AuthFactory() => BifrostAuthContextFactory.Instance;

        // ---- AddBifrostFeeds registers the seams --------------------------------------------------

        [Fact]
        public void AddBifrostFeeds_registers_options_planner_and_authenticator()
        {
            var services = new ServiceCollection();
            services.AddSingleton(AuthFactory());
            services.AddSingleton(Substitute.For<IQueryIntentExecutor>());

            services.AddBifrostFeeds(Feed, o => o.Endpoint = "/graphql");
            using var sp = services.BuildServiceProvider();

            sp.GetService<FeedOptions>().Should().BeSameAs(Feed);
            var endpoint = sp.GetService<FeedEndpointOptions>();
            endpoint.Should().NotBeNull();
            endpoint!.Enabled.Should().BeTrue("AddBifrostFeeds enables the endpoint");
            endpoint.RoutePrefix.Should().Be("/feeds");
            endpoint.Endpoint.Should().Be("/graphql");
            sp.GetService<FeedAuthenticator>().Should().NotBeNull();
            sp.GetService<FeedReadPlanner>().Should().NotBeNull();
        }

        [Fact]
        public void AddBifrostFeeds_honors_a_custom_route_prefix()
        {
            var services = new ServiceCollection();
            services.AddSingleton(AuthFactory());
            services.AddSingleton(Substitute.For<IQueryIntentExecutor>());

            services.AddBifrostFeeds(Feed, o => o.RoutePrefix = "/syndication");
            using var sp = services.BuildServiceProvider();

            sp.GetRequiredService<FeedEndpointOptions>().RoutePrefix.Should().Be("/syndication");
        }

        // ---- UseBifrostFeeds is inert when unregistered -------------------------------------------

        [Fact]
        public async Task UseBifrostFeeds_is_inert_when_the_endpoint_is_not_registered()
        {
            // No AddBifrostFeeds: the branch must not be mounted, so a /feeds request falls through to
            // the terminal delegate untouched (the endpoint alters no route when off).
            using var sp = new ServiceCollection().BuildServiceProvider();
            var app = new ApplicationBuilder(sp);

            var reachedTerminal = false;
            app.UseBifrostFeeds();
            app.Run(ctx => { reachedTerminal = true; ctx.Response.StatusCode = 204; return Task.CompletedTask; });
            var pipeline = app.Build();

            var context = new DefaultHttpContext { RequestServices = sp };
            context.Request.Path = "/feeds/posts.rss";
            await pipeline(context);

            reachedTerminal.Should().BeTrue("an unregistered feed endpoint must not intercept the request");
            context.Response.StatusCode.Should().Be(204);
        }

        [Fact]
        public async Task UseBifrostFeeds_does_not_intercept_a_non_feed_route_when_enabled()
        {
            // The branch is scoped to the prefix: a request OUTSIDE /feeds reaches the terminal delegate,
            // so mounting the endpoint never captures GraphQL/other routes.
            var services = new ServiceCollection();
            services.AddSingleton(AuthFactory());
            services.AddSingleton(Substitute.For<IQueryIntentExecutor>());
            services.AddLogging();
            services.AddBifrostFeeds(Feed);
            using var sp = services.BuildServiceProvider();

            var app = new ApplicationBuilder(sp);
            var reachedTerminal = false;
            app.UseBifrostFeeds();
            app.Run(ctx => { reachedTerminal = true; ctx.Response.StatusCode = 200; return Task.CompletedTask; });
            var pipeline = app.Build();

            var context = new DefaultHttpContext { RequestServices = sp };
            context.Request.Path = "/graphql";
            await pipeline(context);

            reachedTerminal.Should().BeTrue("a non-/feeds route must pass through to the rest of the pipeline");
        }
    }
}

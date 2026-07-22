using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// Mounting configuration for the opt-in feed HTTP endpoint: which branch it listens on and which
    /// registered GraphQL endpoint's cached model/connection its reads resolve against. The feed's
    /// presentation + bounds live on the separate <see cref="FeedOptions"/> the host supplies to
    /// <see cref="BifrostFeedExtensions.AddBifrostFeeds"/>.
    /// </summary>
    public sealed class FeedEndpointOptions
    {
        /// <summary>Whether the feed endpoint is mounted. Set true by <c>AddBifrostFeeds</c>; default false (opt-in).</summary>
        public bool Enabled { get; set; }

        /// <summary>The path branch the endpoint listens under. Default: <c>/feeds</c>.</summary>
        public string RoutePrefix { get; set; } = "/feeds";

        /// <summary>
        /// The registered GraphQL endpoint whose cached model/connection feed reads execute against.
        /// Null selects the single registered endpoint; with multiple endpoints it selects one and an
        /// unknown path fails fast (never a silent fallback to another database).
        /// </summary>
        public string? Endpoint { get; set; }
    }

    /// <summary>
    /// Opt-in registration + mounting for the syndication-feed HTTP front door. A host that never calls
    /// <see cref="AddBifrostFeeds"/> registers nothing and <see cref="UseBifrostFeeds"/> is inert, so the
    /// endpoint is off by construction and never alters the GraphQL/binary/other protocol routes.
    /// </summary>
    public static class BifrostFeedExtensions
    {
        /// <summary>
        /// Registers the opt-in feed endpoint's services: the mounting <see cref="FeedEndpointOptions"/>,
        /// the presentation/bounds <paramref name="feed"/>, the <see cref="FeedAuthenticator"/> (which
        /// resolves the shared <see cref="IBifrostAuthContextFactory"/> and an optional host-supplied
        /// <see cref="IFeedCredentialStore"/> — a Bearer-only deployment registers none and scoped-token
        /// requests then fail closed), and the <see cref="FeedReadPlanner"/> over
        /// <see cref="IQueryIntentExecutor"/>. Idempotent registrations use <c>TryAdd</c> so a host may
        /// override any seam before calling this.
        /// </summary>
        public static IServiceCollection AddBifrostFeeds(
            this IServiceCollection services, FeedOptions feed, Action<FeedEndpointOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(feed);

            var options = new FeedEndpointOptions { Enabled = true };
            configure?.Invoke(options);

            services.AddSingleton(feed);
            services.AddSingleton(options);
            services.TryAddSingleton(sp => new FeedAuthenticator(
                sp.GetRequiredService<IBifrostAuthContextFactory>(),
                sp.GetService<IFeedCredentialStore>(),
                sp.GetService<ILogger<FeedAuthenticator>>()));
            services.TryAddSingleton(sp => new FeedReadPlanner(sp.GetRequiredService<IQueryIntentExecutor>()));
            return services;
        }

        /// <summary>
        /// Mounts the feed endpoint on its own <see cref="FeedEndpointOptions.RoutePrefix"/> branch when
        /// it has been enabled via <see cref="AddBifrostFeeds"/>. A no-op when unregistered/disabled, so a
        /// host can call it unconditionally, and it never alters existing routes.
        ///
        /// <para>Bearer identity is read from <c>HttpContext.User</c>, so a host accepting Bearer tokens
        /// must run its authentication middleware (<c>UseAuthentication</c>) before this call. Enabling the
        /// endpoint logs a startup warning, since exposing a data front door is a posture change worth
        /// surfacing. NOTE: the <c>?token=</c> query credential is supported for feed readers that cannot
        /// send an Authorization header, but a query-string token LEAKS into intermediary/access logs;
        /// prefer the <c>Authorization: Bearer</c> header, which takes precedence, and note every
        /// query-token response is served <c>Cache-Control: private, no-store</c>.</para>
        /// </summary>
        public static IApplicationBuilder UseBifrostFeeds(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var options = app.ApplicationServices.GetService<FeedEndpointOptions>();
            if (options is null || !options.Enabled)
                return app;

            app.ApplicationServices.GetService<ILoggerFactory>()
                ?.CreateLogger("BifrostQL.Server.Feeds")
                .LogWarning("Syndication feed HTTP endpoint is ENABLED at prefix '{Prefix}'. " +
                    "This exposes an authenticated data front door; ensure Bearer auth and/or the feed " +
                    "credential store are trusted. Query-string ?token= credentials leak into access logs.",
                    options.RoutePrefix);

            app.Map(options.RoutePrefix, branch => branch.UseMiddleware<FeedMiddleware>());
            return app;
        }
    }
}

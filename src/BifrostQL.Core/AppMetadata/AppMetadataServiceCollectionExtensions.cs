using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Dependency-injection registration for the app-metadata overlay.
    ///
    /// <see cref="AddBifrostAppMetadata"/> registers the overlay as a separate,
    /// coexisting service: it adds a lazily-loaded <see cref="AppMetadataModel"/>
    /// (as <c>Lazy&lt;Task&lt;AppMetadataModel&gt;&gt;</c>, loaded once off the
    /// request thread) and the <see cref="AppMetadataLoader"/> that builds it,
    /// without touching any service registered by <c>AddBifrostQL</c>. The overlay can therefore
    /// be added before or after <c>AddBifrostQL</c>, or omitted entirely, with
    /// no effect on the schema-metadata pipeline.
    /// </summary>
    public static class AppMetadataServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the app-metadata overlay from the supplied sources. The
        /// overlay is loaded once and exposed as a singleton
        /// <see cref="AppMetadataModel"/>; the <see cref="AppMetadataLoader"/>
        /// used to build it is also registered for callers that want to reload.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="sources">
        /// The overlay sources, merged in priority order. Higher-priority
        /// sources override lower-priority entries for the same qualified table
        /// name. An empty list registers an empty overlay.
        /// </param>
        public static IServiceCollection AddBifrostAppMetadata(
            this IServiceCollection services,
            IReadOnlyList<IAppMetadataSource> sources)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(sources);

            var source = sources.Count == 1
                ? sources[0]
                : new CompositeAppMetadataSource(sources);
            var loader = new AppMetadataLoader(source);

            services.AddSingleton(loader);
            // Memoize the overlay load as a Lazy<Task<>> so it runs once, lazily,
            // off the request thread — never blocking with GetAwaiter().GetResult().
            services.AddSingleton(_ => new Lazy<Task<AppMetadataModel>>(() => loader.LoadAsync()));

            return services;
        }

        /// <summary>
        /// Registers the app-metadata overlay from a single source. Convenience
        /// overload of <see cref="AddBifrostAppMetadata(IServiceCollection,IReadOnlyList{IAppMetadataSource})"/>.
        /// </summary>
        public static IServiceCollection AddBifrostAppMetadata(
            this IServiceCollection services,
            IAppMetadataSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return services.AddBifrostAppMetadata(new[] { source });
        }
    }
}

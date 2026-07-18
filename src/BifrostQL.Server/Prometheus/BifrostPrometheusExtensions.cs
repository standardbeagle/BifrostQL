using System;
using BifrostQL.Core.Observers;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Aggregate configuration for the opt-in Prometheus <c>/metrics</c> front door, passed to
    /// <see cref="BifrostPrometheusExtensions.AddBifrostPrometheus"/>. It bundles the three existing
    /// slice option types so a host configures the whole surface in one place:
    /// <list type="bullet">
    /// <item><see cref="Security"/> — the fail-closed credential gate + scoping authority (business
    /// metrics OFF by default; armed only when both enabled AND a credential are set).</item>
    /// <item><see cref="Exposition"/> — the route, backing GraphQL endpoint, single-flight cache TTL,
    /// cardinality backstop, and request bounds.</item>
    /// <item><see cref="Collection"/> — the per-metric aggregate query timeout.</item>
    /// </list>
    /// </summary>
    public sealed class PrometheusServerOptions
    {
        public PrometheusScrapeSecurityOptions Security { get; set; } = new();
        public PrometheusExpositionOptions Exposition { get; set; } = new();
        public PrometheusCollectionOptions Collection { get; set; } = new();
    }

    /// <summary>
    /// Registration + pipeline wiring for the opt-in Prometheus <c>/metrics</c> exposition endpoint.
    /// <see cref="AddBifrostPrometheus"/> registers the scrape gate, scope resolver, series collector,
    /// scrape service, and the engine self-metrics registry + its read/write observers;
    /// <see cref="UseBifrostPrometheus"/> mounts the middleware on its own branch. Both are opt-in and
    /// inert when not configured (mirroring <c>AddBifrostGrpc</c>/<c>MapBifrostGrpc</c> and
    /// <c>UseBifrostOData</c>): a host may call <see cref="UseBifrostPrometheus"/> unconditionally.
    ///
    /// <para>Security posture is unchanged from slices 3-4: business metrics default OFF, the credential
    /// gate is the FIRST check in the middleware, denial is uniform (absent ≡ wrong ≡ disabled), and
    /// every aggregate crosses <see cref="IQueryIntentExecutor"/> under a resolved scope — never an
    /// ambient identity. Arming the surface logs the slice-3 posture warning (the gate does this on
    /// construction; the mount resolves it eagerly so the warning fires at startup, not first scrape).</para>
    /// </summary>
    public static class BifrostPrometheusExtensions
    {
        public static IServiceCollection AddBifrostPrometheus(
            this IServiceCollection services, Action<PrometheusServerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new PrometheusServerOptions();
            configure(options);

            services.AddSingleton(options.Security);
            services.AddSingleton(options.Exposition);
            services.AddSingleton(options.Collection);

            // The shared identity seam every transport gate uses; TryAdd so a host that already
            // registered its own factory keeps it.
            services.TryAddSingleton<IBifrostAuthContextFactory>(BifrostAuthContextFactory.Instance);

            // The gate logs the arming posture warning on construction (invariant 2 / slice-3).
            services.TryAddSingleton(sp => new PrometheusScrapeGate(
                options.Security, sp.GetService<ILogger<PrometheusScrapeGate>>()));
            services.TryAddSingleton(sp => new PrometheusScrapeScopeResolver(
                options.Security, sp.GetService<IBifrostAuthContextFactory>()));
            services.TryAddSingleton(sp => new PrometheusSeriesCollector(
                sp.GetRequiredService<IQueryIntentExecutor>(), options.Collection));

            // Engine self-metrics registry: ENABLED because a scrape surface is configured (criterion
            // 2). A host with no scrape surface never registers this, so EngineMetrics is absent and
            // every engine record site (observers + SqlExecutionManager) no-ops (fully inert).
            services.TryAddSingleton(new EngineMetrics(enabled: true));

            // Register the slice-5 read/write observers as IQueryObserver / IMutationObserver
            // singletons. The observer-collection factories in BifrostServiceRegistrar compose
            // DI-registered observers in additively, so these feed the live query/mutation pipeline
            // regardless of whether AddBifrostPrometheus runs before or after AddBifrostQL.
            services.AddSingleton<BifrostQL.Core.Modules.IQueryObserver>(sp =>
                new EngineMetricsQueryObserver(sp.GetRequiredService<EngineMetrics>()));
            services.AddSingleton<BifrostQL.Core.Modules.IMutationObserver>(sp =>
                new EngineMetricsMutationObserver(sp.GetRequiredService<EngineMetrics>()));

            services.TryAddSingleton(sp => new PrometheusScrapeService(
                sp.GetRequiredService<IQueryIntentExecutor>(),
                sp.GetRequiredService<PrometheusScrapeScopeResolver>(),
                sp.GetRequiredService<PrometheusSeriesCollector>(),
                options.Exposition,
                clock: null,
                logger: sp.GetService<ILogger<PrometheusScrapeService>>(),
                engineMetrics: sp.GetRequiredService<EngineMetrics>()));

            return services;
        }

        /// <summary>
        /// Mounts the opt-in Prometheus <c>/metrics</c> endpoint when it has been registered via
        /// <see cref="AddBifrostPrometheus"/>. A no-op when it was not registered, so a host can call
        /// it unconditionally, and it never alters the existing GraphQL/binary routes — the endpoint
        /// is mounted on its own branch at <see cref="PrometheusExpositionOptions.RoutePath"/>.
        /// Eagerly resolves the credential gate so the slice-3 arming posture warning fires at startup.
        /// </summary>
        public static IApplicationBuilder UseBifrostPrometheus(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var options = app.ApplicationServices.GetService<PrometheusExpositionOptions>();
            if (options is null)
                return app; // not configured → inert.

            // Force gate construction now so the arming posture warning is logged at startup, not on
            // the first scrape (the gate is otherwise a lazily-resolved singleton).
            _ = app.ApplicationServices.GetService<PrometheusScrapeGate>();

            app.Map(options.RoutePath, branch =>
                branch.UseMiddleware<PrometheusMetricsMiddleware>());
            return app;
        }
    }
}

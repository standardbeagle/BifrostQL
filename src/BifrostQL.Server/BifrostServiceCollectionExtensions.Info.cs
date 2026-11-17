using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    public static partial class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the BifrostQL info endpoint and server header middleware.
        /// Call before UseBifrostQL/UseBifrostEndpoints in the pipeline.
        /// </summary>
        public static IApplicationBuilder UseBifrostInfo(this IApplicationBuilder app, Action<BifrostInfoOptions>? configure = null)
        {
            var options = new BifrostInfoOptions();
            configure?.Invoke(options);

            // Register the server clock singleton if not already registered
            var clock = app.ApplicationServices.GetService<BifrostServerClock>();
            if (clock == null)
            {
                throw new InvalidOperationException(
                    "BifrostServerClock not registered. Call AddBifrostInfo() in service configuration.");
            }

            app.UseMiddleware<BifrostHeaderMiddleware>();

            if (options.Enabled)
            {
                app.UseMiddleware<BifrostInfoMiddleware>(options);
            }

            return app;
        }

        /// <summary>
        /// Registers required services for the BifrostQL info endpoint.
        /// Call during service configuration (before Build).
        /// </summary>
        public static IServiceCollection AddBifrostInfo(this IServiceCollection services)
        {
            services.AddSingleton<BifrostServerClock>();
            return services;
        }

        /// <summary>
        /// Maps the app-metadata overlay endpoint, which serves the loaded
        /// <see cref="BifrostQL.Core.AppMetadata.AppMetadataModel"/> as the stable
        /// camelCase JSON contract for SPA and React Native clients. The endpoint
        /// returns an empty overlay when none has been registered via
        /// <c>AddBifrostAppMetadata</c>, so it always serves the stable contract.
        ///
        /// <para>Mount it after <c>UseBifrostQL()</c> / <c>UseBifrostEndpoints()</c>: those add the
        /// authentication middleware, and a mount ahead of it sees every caller as anonymous (it then
        /// refuses them all when auth is required; it never serves them). The auth requirement is the
        /// served endpoint's, derived here by <c>MountAuthRequirement</c> unless
        /// <see cref="BifrostAppMetadataOptions.RequireAuth"/> overrides it.</para>
        /// </summary>
        public static IApplicationBuilder UseBifrostAppMetadata(
            this IApplicationBuilder app,
            Action<BifrostAppMetadataOptions>? configure = null)
        {
            var options = new BifrostAppMetadataOptions();
            configure?.Invoke(options);

            var requiresAuth = options.RequireAuth
                ?? MountAuthRequirement.Resolve(app.ApplicationServices, options.GraphQlEndpoint);
            if (options.Enabled)
                app.UseMiddleware<BifrostAppMetadataMiddleware>(options, requiresAuth);

            return app;
        }
    }
}

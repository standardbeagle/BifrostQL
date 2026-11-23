using BifrostQL.Core.SavedObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    /// <summary>
    /// Registration + pipeline wiring for the saved-object store, mirroring the
    /// <c>AddBifrostAppMetadata</c> / <c>UseBifrostAppMetadata</c> pair. The deployment
    /// supplies the backing <see cref="ISavedObjectStore"/> (file-backed for desktop,
    /// DB-backed for hosted), keeping the store choice out of the middleware.
    /// </summary>
    public static class BifrostSavedObjectsExtensions
    {
        /// <summary>Registers the saved-object store implementation for the endpoint to resolve.</summary>
        public static IServiceCollection AddBifrostSavedObjects(this IServiceCollection services, ISavedObjectStore store)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(store);
            services.AddSingleton(store);
            return services;
        }

        /// <summary>
        /// Adds the <c>/_saved-objects</c> CRUD middleware to the pipeline. Mount it after
        /// <c>UseBifrostQL()</c> / <c>UseBifrostEndpoints()</c>: a mount ahead of the authentication
        /// middleware they add sees every caller as anonymous. The auth requirement comes from
        /// <c>MountAuthRequirement</c> unless <see cref="BifrostSavedObjectsOptions.RequireAuth"/>
        /// overrides it.
        ///
        /// <para>Refuses to mount when <see cref="BifrostSavedObjectsOptions.GraphQlEndpoint"/> cannot
        /// name one registered endpoint: null with several registered, or a path none is registered
        /// under. Both used to pass here (the auth requirement's single-endpoint fallback masked a
        /// misspelled path) and then fail every signed-in request.</para>
        /// </summary>
        public static IApplicationBuilder UseBifrostSavedObjects(
            this IApplicationBuilder app,
            Action<BifrostSavedObjectsOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(app);
            var options = new BifrostSavedObjectsOptions();
            configure?.Invoke(options);
            if (options.Enabled)
                MountAuthRequirement.RequireOneServedEndpoint(app.ApplicationServices, options.GraphQlEndpoint,
                    nameof(UseBifrostSavedObjects), $"{nameof(BifrostSavedObjectsOptions)}.{nameof(BifrostSavedObjectsOptions.GraphQlEndpoint)}");
            // The endpoint whose model keys the owner partition also supplies the requirement: a
            // null path resolves to the single registered endpoint's, and fails closed with none.
            // Several with none named, or an unregistered name, were refused above.
            var requiresAuth = options.RequireAuth
                ?? MountAuthRequirement.Resolve(app.ApplicationServices, options.GraphQlEndpoint);
            if (options.Enabled)
                app.UseMiddleware<BifrostSavedObjectsMiddleware>(options, requiresAuth);
            return app;
        }
    }
}

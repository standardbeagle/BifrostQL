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

        /// <summary>Adds the <c>/_saved-objects</c> CRUD middleware to the pipeline.</summary>
        public static IApplicationBuilder UseBifrostSavedObjects(
            this IApplicationBuilder app,
            Action<BifrostSavedObjectsOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(app);
            var options = new BifrostSavedObjectsOptions();
            configure?.Invoke(options);
            if (options.Enabled)
                app.UseMiddleware<BifrostSavedObjectsMiddleware>(options);
            return app;
        }
    }
}

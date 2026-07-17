using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Pipeline wiring for the opt-in OData endpoint.
    /// </summary>
    public static class ODataApplicationBuilderExtensions
    {
        /// <summary>
        /// Mounts the opt-in OData v4 HTTP endpoint when it has been enabled via
        /// <see cref="BifrostSetupOptions.AddODataEndpoint"/> /
        /// <see cref="BifrostMultiDbOptions.AddODataEndpoint"/>. A no-op when the endpoint was
        /// not enabled, so a host can call it unconditionally, and it never alters the existing
        /// GraphQL/binary routes — the adapter is mounted on its own branch at
        /// <see cref="ODataOptions.RoutePrefix"/>.
        ///
        /// <para>Bearer identity is read from <c>HttpContext.User</c>, so a host accepting Bearer
        /// tokens must run its authentication middleware (<c>UseAuthentication</c>) before this
        /// call. Basic credentials are resolved through a host-supplied
        /// <see cref="IODataBasicCredentialStore"/>; a deployment accepting only Bearer registers
        /// none and Basic requests then fail closed with 401. Enabling the endpoint logs a
        /// startup warning, since exposing an OData front door is a posture change worth
        /// surfacing.</para>
        /// </summary>
        public static IApplicationBuilder UseBifrostOData(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<ODataOptions>();
            if (options is null || !options.Enabled)
                return app;

            app.ApplicationServices.GetService<ILoggerFactory>()
                ?.CreateLogger("BifrostQL.Server.OData")
                .LogWarning("OData v4 HTTP endpoint is ENABLED at prefix '{Prefix}'. " +
                    "This exposes an authenticated front door; ensure Bearer auth and/or the " +
                    "Basic credential store are trusted.", options.RoutePrefix);

            app.Map(options.RoutePrefix, branch =>
                branch.UseMiddleware<ODataMiddleware>(options));
            return app;
        }
    }
}

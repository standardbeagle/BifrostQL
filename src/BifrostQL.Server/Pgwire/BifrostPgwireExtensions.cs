using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Registration for the PostgreSQL wire-protocol front door. Wires the per-connection
    /// <see cref="PgConnectionHandler"/> onto a plain-TCP Kestrel listener (TLS is
    /// negotiated inside the handler on SSLRequest, not by Kestrel HTTPS) and registers
    /// the <see cref="PgWireAdapter"/> lifecycle via the standard adapter/hosted-service
    /// pattern.
    /// </summary>
    public static class BifrostPgwireExtensions
    {
        /// <summary>
        /// Adds the pgwire front door. The host must separately register an
        /// <see cref="IPgCredentialStore"/> (the identity source pg logins authenticate
        /// against) and set <see cref="PgWireOptions.ServerCertificate"/> — both are
        /// hard requirements, enforced fail-fast at startup, so the port can never come
        /// up authenticating anonymously or without TLS.
        /// </summary>
        public static IServiceCollection AddBifrostPgwire(this IServiceCollection services, Action<PgWireOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new PgWireOptions();
            configure(options);
            services.AddSingleton(options);

            // The per-connection handler is resolved by the Kestrel listener from DI.
            services.TryAddSingleton<PgConnectionHandler>();

            // Adapter lifecycle via the shared adapter/hosted-service pattern.
            services.TryAddSingleton<PgWireAdapter>();
            services.AddSingleton<IHostedService>(sp =>
                new ProtocolAdapterHostedService(sp.GetRequiredService<PgWireAdapter>()));

            // Bind a plain-TCP listener; the handler answers SSLRequest and upgrades to
            // TLS itself (STARTTLS-style), which Kestrel HTTPS cannot express.
            services.PostConfigure<KestrelServerOptions>(kestrel =>
                kestrel.ListenAnyIP(options.Port, listen =>
                    listen.UseConnectionHandler<PgConnectionHandler>()));

            return services;
        }
    }
}

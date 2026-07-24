using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Registration for the opt-in LDAPv3 protocol front door. Wires the per-connection
    /// <see cref="LdapConnectionHandler"/> onto a plain-TCP Kestrel listener and registers the
    /// <see cref="LdapWireAdapter"/> lifecycle via the standard adapter/hosted-service pattern
    /// (mirrors <c>AddBifrostResp</c> / <c>AddBifrostPgwire</c>). The front door does not exist
    /// unless a host calls this — there is no ambient LDAP listener.
    /// </summary>
    public static class BifrostLdapExtensions
    {
        /// <summary>
        /// Adds the LDAP front door. The options are validated eagerly here so a misconfigured limit
        /// (or an illegal port) fails at registration rather than after the listener binds, and again
        /// at <see cref="LdapWireAdapter.StartAsync"/> so host startup aborts on any invalid config.
        /// </summary>
        public static IServiceCollection AddBifrostLdap(this IServiceCollection services, Action<LdapWireOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new LdapWireOptions();
            configure(options);
            LdapWireAdapter.Validate(options);
            services.AddSingleton(options);

            // One process-wide connection limiter shared across every connection.
            services.TryAddSingleton(new LdapBoundedCounter(options.MaxConnections, "MaxConnections"));

            // A single per-adapter connection handler resolved once by the Kestrel listener from DI.
            services.TryAddSingleton<LdapConnectionHandler>();

            // Adapter lifecycle via the shared adapter/hosted-service pattern.
            services.TryAddSingleton<LdapWireAdapter>();
            services.AddSingleton<IHostedService>(sp =>
                new ProtocolAdapterHostedService(sp.GetRequiredService<LdapWireAdapter>()));

            // Bind a plain-TCP listener; the handler speaks LDAP/BER directly on the raw socket.
            services.PostConfigure<KestrelServerOptions>(kestrel =>
                kestrel.ListenAnyIP(options.Port, listen =>
                    listen.UseConnectionHandler<LdapConnectionHandler>()));

            return services;
        }
    }
}

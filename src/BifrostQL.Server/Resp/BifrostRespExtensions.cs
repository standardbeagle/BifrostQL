using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Registration for the Redis RESP-protocol front door. Wires the per-connection
    /// <see cref="RespConnectionHandler"/> onto a plain-TCP Kestrel listener and registers the
    /// <see cref="RespWireAdapter"/> lifecycle via the standard adapter/hosted-service pattern
    /// (mirrors <c>AddBifrostPgwire</c>).
    /// </summary>
    public static class BifrostRespExtensions
    {
        /// <summary>
        /// Adds the RESP front door. The host must separately register an
        /// <see cref="IRespCredentialStore"/> (the identity source AUTH authenticates against) —
        /// a hard requirement, resolved fail-fast at startup, so an auth-required port can never
        /// come up without an identity source. Data command handlers
        /// (<see cref="IRespCommandHandler"/>) are registered separately by later slices and are
        /// picked up automatically by the connection handler's dispatch table.
        /// </summary>
        public static IServiceCollection AddBifrostResp(this IServiceCollection services, Action<RespWireOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new RespWireOptions();
            configure(options);
            services.AddSingleton(options);

            // The per-connection handler is resolved by the Kestrel listener from DI. A single
            // instance is shared across all connections (Kestrel resolves it once).
            services.TryAddSingleton<RespConnectionHandler>();

            // Adapter lifecycle via the shared adapter/hosted-service pattern.
            services.TryAddSingleton<RespWireAdapter>();
            services.AddSingleton<IHostedService>(sp =>
                new ProtocolAdapterHostedService(sp.GetRequiredService<RespWireAdapter>()));

            // Bind a plain-TCP listener; the handler speaks RESP directly on the raw socket.
            services.PostConfigure<KestrelServerOptions>(kestrel =>
                kestrel.ListenAnyIP(options.Port, listen =>
                    listen.UseConnectionHandler<RespConnectionHandler>()));

            return services;
        }
    }
}

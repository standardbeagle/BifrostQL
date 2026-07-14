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

            // Slice-2 read commands attach at the IRespCommandHandler seam — the connection handler
            // indexes every registered handler by name for dispatch, with no edit to the loop. All
            // reads route through IQueryIntentExecutor under the session identity, so the security
            // transformer pipeline is unskippable.
            services.AddSingleton<IRespCommandHandler, RespGetCommandHandler>();
            services.AddSingleton<IRespCommandHandler, RespMGetCommandHandler>();
            services.AddSingleton<IRespCommandHandler, RespExistsCommandHandler>();
            services.AddSingleton<IRespCommandHandler, RespTypeCommandHandler>();

            // Slice-3 hash commands reuse the same single-row read path; the row is projected as a
            // field/value hash (HGETALL) or a single visible column (HGET).
            services.AddSingleton<IRespCommandHandler, RespHGetAllCommandHandler>();
            services.AddSingleton<IRespCommandHandler, RespHGetCommandHandler>();

            // Slice-4 SCAN maps <table>:* to keyset pagination over the table's primary key, enumerated
            // through IQueryIntentExecutor under the session identity so only visible PKs are emitted.
            services.AddSingleton<IRespCommandHandler, RespScanCommandHandler>();

            // Slice-5 WRITE commands (SET/HSET/DEL) route through IMutationIntentExecutor under the
            // session identity, so the full mutation transformer chain (tenant scoping, audit actor,
            // soft-delete, field-encryption-on-write, CDC/history hooks) is unskippable. They are gated
            // OFF BY DEFAULT: each handler refuses with a clean -ERR and executes nothing unless the
            // deployment set RespWireOptions.EnableWrites — registering them here is inert until then.
            services.AddSingleton<IRespCommandHandler, RespSetCommandHandler>();
            services.AddSingleton<IRespCommandHandler, RespHSetCommandHandler>();
            services.AddSingleton<IRespCommandHandler, RespDelCommandHandler>();

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

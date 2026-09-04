using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Registration for the Redis RESP-protocol front door. Wires the per-connection
    /// <see cref="RespConnectionHandler"/> onto a Kestrel listener (plain TCP by default;
    /// TLS-terminated when <see cref="RespWireOptions.ServerCertificate"/> is set) and registers
    /// the <see cref="RespWireAdapter"/> lifecycle via the standard adapter/hosted-service pattern
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
            // instance is shared across all connections (Kestrel resolves it once), so the
            // admission counter it consults must be the SAME instance for every connection. It is
            // captured here rather than resolved later because the admission middleware below runs
            // outside the DI-resolved handler, and both must consult the one counter.
            var connectionLimiter = new RespConnectionLimiter(options.MaxConnections);
            services.TryAddSingleton(connectionLimiter);
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

            // The SCAN cursor's MAC key is resolved ONCE (a per-call random key would make every
            // issued cursor fail its own validation), like the gRPC page-token key.
            services.TryAddSingleton(sp => RespScanCursorKey.Resolve(
                options, sp.GetRequiredService<ILoggerFactory>().CreateLogger<RespScanCursorKey>()));

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
            services.AddSingleton<IRespCommandHandler, RespMSetCommandHandler>();

            // Adapter lifecycle via the shared adapter/hosted-service pattern.
            services.TryAddSingleton<RespWireAdapter>();
            services.AddSingleton<IHostedService>(sp =>
                new ProtocolAdapterHostedService(sp.GetRequiredService<RespWireAdapter>()));

            // Bind the listener; the handler speaks RESP on the (optionally TLS-terminated) socket.
            //
            // Bound to RespWireOptions.BindAddress, which DEFAULTS TO LOOPBACK. This was
            // ListenAnyIP (0.0.0.0) with no override, so registering the adapter published a Redis
            // front door on every network the host sits on — a posture decision nobody made.
            // Widening it is now explicit in the host's own startup code.
            //
            // When RespWireOptions.ServerCertificate is set, Kestrel performs the TLS handshake
            // BEFORE the connection handler sees any byte, so AUTH credentials never cross the
            // wire in the clear. Registered before UseConnectionHandler: the HTTPS connection
            // middleware must wrap the handler (Kestrel runs connection middleware in
            // registration order).
            services.PostConfigure<KestrelServerOptions>(kestrel =>
                kestrel.Listen(options.BindAddress, options.Port, listen =>
                {
                    // ADMISSION FIRST — ahead of UseHttps. Kestrel composes connection middleware
                    // so the first-registered runs outermost, which is the only place the slot can
                    // be reserved at ACCEPT. Held inside the connection handler it was taken after
                    // the TLS handshake, so a peer that never sent a ClientHello cost a connection
                    // and a TLS state machine the cap could not see: that bounds admitted sessions,
                    // not the work an unauthenticated peer can force. Mirrors the gRPC listener.
                    listen.Use(next => async connection =>
                    {
                        if (!connectionLimiter.TryAcquire())
                        {
                            await RefuseAsync(connection, options.ServerCertificate is null);
                            return;
                        }
                        // Tells the handler the slot is already held, so it does not take a second
                        // one for the same connection and halve the effective cap.
                        connection.Items[RespConnectionHandler.AdmittedItemKey] = true;
                        try
                        {
                            await next(connection);
                        }
                        finally
                        {
                            connectionLimiter.Release();
                        }
                    });
                    if (options.ServerCertificate is not null)
                        listen.UseHttps(options.ServerCertificate);
                    listen.UseConnectionHandler<RespConnectionHandler>();
                }));

            return services;
        }

        /// <summary>
        /// Turns away an over-cap connection. On a cleartext listener the peer gets the RESP
        /// refusal it can actually parse; on a TLS listener the refusal happens before the
        /// handshake, where no RESP byte can be spoken, so the connection is simply aborted —
        /// writing plaintext ahead of a ClientHello would desync the client instead of informing
        /// it. Either way nothing is read from the peer and the slot is never held.
        /// </summary>
        private static async Task RefuseAsync(ConnectionContext connection, bool cleartext)
        {
            if (cleartext)
            {
                await connection.Transport.Output.WriteAsync(
                    Encoding.ASCII.GetBytes($"-{RespProtocol.TooManyConnectionsError}\r\n"));
            }
            await connection.Transport.Output.CompleteAsync();
            connection.Abort();
        }
    }
}

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

            // Shared, front-door-lifetime coordination objects (slice 5): the CancelRequest ⇄
            // BackendKeyData table and the lock-free connection-admission counter. Both must be
            // singletons so every connection consults the same state.
            services.TryAddSingleton<PgCancellationRegistry>();
            services.TryAddSingleton(new PgConnectionLimiter(options.MaxConnections));

            // The per-connection handler is resolved by the Kestrel listener from DI.
            services.TryAddSingleton<PgConnectionHandler>();

            // SQL-subset translator (slice 3): parses SELECT + WHERE / ORDER BY / LIMIT /
            // OFFSET + a single schema-relationship JOIN into a programmatic GqlObjectQuery.
            // Registered behind IPgQueryTranslator so it swaps in without touching the query
            // loop or result encoding. Stateless — the executor is passed per query.
            services.TryAddSingleton<IPgQueryTranslator, PgSubsetQueryTranslator>();

            // Catalog emulation (slice 4): answers pg_catalog/information_schema
            // introspection (psql \d, BI-tool schema discovery) from a DbModel-derived,
            // identity-filtered projection, before the SQL read path. Consulted per query
            // by the connection handler; returns null for non-catalog queries.
            services.TryAddSingleton<IPgCatalogResponder, PgCatalogResponder>();

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

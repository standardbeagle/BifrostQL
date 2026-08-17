using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

            // The simple-bind authenticator exists ONLY when a deployment has registered BOTH
            // required seams — an ILdapCredentialStore (resolve DN -> hash) and an ILdapPasswordHasher
            // (bcrypt/Argon2id verify). Absent either, the handler is built without an authenticator
            // and refuses binds with unwillingToPerform: there is no default ambient credential
            // store and no plaintext path, so authentication is fail-closed by construction (criterion 1).
            // A single per-adapter connection handler resolved once by the Kestrel listener from DI.
            services.TryAddSingleton<LdapConnectionHandler>(sp =>
            {
                var store = sp.GetService<ILdapCredentialStore>();
                var hasher = sp.GetService<ILdapPasswordHasher>();
                var authenticator = store is null || hasher is null
                    ? null
                    : new LdapBindAuthenticator(
                        store, hasher,
                        sp.GetService<IBifrostAuthContextFactory>() ?? BifrostAuthContextFactory.Instance,
                        options,
                        services: sp,
                        rateLimiter: null,
                        observer: sp.GetService<ILdapBindObserver>(),
                        clock: null,
                        logger: sp.GetService<ILogger<LdapBindAuthenticator>>());
                return new LdapConnectionHandler(
                    options,
                    sp.GetRequiredService<LdapBoundedCounter>(),
                    authenticator,
                    sp.GetService<ILogger<LdapConnectionHandler>>());
            });

            // Adapter lifecycle via the shared adapter/hosted-service pattern.
            services.TryAddSingleton<LdapWireAdapter>();
            services.AddSingleton<IHostedService>(sp =>
                new ProtocolAdapterHostedService(sp.GetRequiredService<LdapWireAdapter>()));

            // Bind a plain-TCP listener; the handler speaks LDAP/BER directly on the raw socket. The
            // bind address is the declared exposure posture — loopback unless the host widened it
            // explicitly — never an ambient wildcard (AGENTS.md "Listener exposure posture").
            services.PostConfigure<KestrelServerOptions>(kestrel =>
                kestrel.Listen(options.BindAddress, options.Port, listen =>
                    listen.UseConnectionHandler<LdapConnectionHandler>()));

            return services;
        }
    }
}

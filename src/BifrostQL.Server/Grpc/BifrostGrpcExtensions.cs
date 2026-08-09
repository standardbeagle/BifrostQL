using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Registration for the opt-in gRPC HTTP/2 front door. <see cref="AddBifrostGrpc"/> wires the
    /// dynamic dispatch service, the identity-filtered reflection service, and the
    /// <see cref="GrpcWireAdapter"/> lifecycle, and binds a dedicated HTTP/2 Kestrel listener on the
    /// configured port (a bind failure there aborts startup — fail-fast). <see cref="MapBifrostGrpc"/>
    /// exposes the gRPC routes; the host calls both (mirrors the RESP/pgwire adapter pattern, adapted
    /// for endpoint-routed HTTP/2).
    /// </summary>
    public static class BifrostGrpcExtensions
    {
        public static IServiceCollection AddBifrostGrpc(this IServiceCollection services, Action<GrpcWireOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new GrpcWireOptions();
            configure(options);
            services.AddSingleton(options);

            // gRPC HTTP/2 hosting + the dynamic service seam. The method set is generated from the
            // model at endpoint-build time (no compiled stubs), and reflection is our own
            // identity-filtered implementation — NOT the built-in AddGrpcReflection, which serves one
            // global descriptor set and cannot filter per caller (invariant 4).
            services.AddGrpc();

            services.TryAddSingleton<GrpcContractProvider>();
            services.TryAddSingleton<IBifrostAuthContextFactory>(BifrostAuthContextFactory.Instance);

            // The List page-token HMAC key is resolved ONCE (a per-call random key would make every
            // issued token fail its own validation). Configured secret → portable; absent → per-instance
            // random key with a logged trade-off, mirroring the OData continuation-token key.
            services.TryAddSingleton(sp => GrpcPageTokenKey.Resolve(
                options, sp.GetRequiredService<ILoggerFactory>().CreateLogger<GrpcPageTokenKey>()));

            services.TryAddEnumerable(ServiceDescriptor.Singleton<
                IServiceMethodProvider<BifrostDynamicGrpcService>, BifrostGrpcServiceMethodProvider>());
            services.AddScoped<BifrostDynamicGrpcService>();
            services.AddScoped<GrpcReflectionService>();

            // Adapter lifecycle via the shared adapter/hosted-service pattern.
            services.TryAddSingleton<GrpcWireAdapter>();
            services.AddSingleton<IHostedService>(sp =>
                new ProtocolAdapterHostedService(sp.GetRequiredService<GrpcWireAdapter>()));

            // A dedicated HTTP/2 listener for the gRPC wire. HTTP/2 is required for gRPC framing +
            // trailers; a bind failure on this port aborts host startup.
            //
            // Bound to GrpcWireOptions.BindAddress, which DEFAULTS TO LOOPBACK. This was
            // ListenAnyIP (0.0.0.0) with no override, so registering the adapter exposed the port
            // to every network the host sits on — a posture decision nobody made. Widening it is
            // now explicit in the host's own startup code.
            services.PostConfigure<KestrelServerOptions>(kestrel =>
            {
                // Kestrel's default is UNLIMITED concurrent connections, so the gRPC front door had
                // no bound on what an unauthenticated peer could consume, unlike pgwire and RESP.
                kestrel.Limits.MaxConcurrentConnections = options.MaxConcurrentConnections;
                kestrel.Listen(options.BindAddress, options.Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    if (options.RequireTls)
                        ConfigureTls(listen, options);
                });
            });

            return services;
        }

        /// <summary>
        /// Serves the configured certificate on the gRPC listener. <see cref="GrpcWireOptions.RequireTls"/>
        /// used to be validated and then IGNORED — the listener was left as cleartext h2c while startup
        /// logged "TLS: True", so every bearer credential crossed the wire in the clear and the operator
        /// was told the opposite. A guard that reads as protection and does nothing at runtime is worse
        /// than none.
        ///
        /// <para>Any load failure is a startup ABORT with an actionable message — never a silent fall
        /// back to cleartext, which would restore exactly the condition this exists to prevent.</para>
        /// </summary>
        private static void ConfigureTls(ListenOptions listen, GrpcWireOptions options)
        {
            // GrpcWireAdapter validates this too, but its hosted service can run AFTER Kestrel binds;
            // this is the check that actually stands between the config and an open port.
            if (string.IsNullOrWhiteSpace(options.TlsCertificatePath))
                throw new GrpcConfigurationException(
                    "gRPC RequireTls is set but no TlsCertificatePath was configured; refusing to bind "
                    + $"port {options.Port} as cleartext.");

            try
            {
                listen.UseHttps(options.TlsCertificatePath, options.TlsCertificatePassword);
            }
            catch (Exception ex)
            {
                throw new GrpcConfigurationException(
                    $"gRPC RequireTls is set but the TLS certificate at '{options.TlsCertificatePath}' "
                    + "could not be loaded. It must be a PKCS#12 (.pfx) file containing the private key, "
                    + "and TlsCertificatePassword must match. Refusing to bind port "
                    + $"{options.Port} as cleartext.",
                    ex);
            }
        }

        /// <summary>
        /// Maps the dynamic Get/List/Stream service and the identity-filtered reflection service into
        /// the endpoint pipeline. Requires <c>UseRouting</c>/endpoint routing (the default in minimal
        /// hosting).
        /// </summary>
        public static void MapBifrostGrpc(this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            endpoints.MapGrpcService<BifrostDynamicGrpcService>();
            endpoints.MapGrpcService<GrpcReflectionService>();
        }
    }
}

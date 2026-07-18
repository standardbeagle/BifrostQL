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
            services.PostConfigure<KestrelServerOptions>(kestrel =>
                kestrel.ListenAnyIP(options.Port, listen => listen.Protocols = HttpProtocols.Http2));

            return services;
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

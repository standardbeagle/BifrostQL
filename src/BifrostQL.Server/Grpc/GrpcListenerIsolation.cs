using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Keeps the gRPC front door and the host's HTTP pipeline on their own listeners. Kestrel runs
    /// ONE request pipeline for every listener it binds, so without this the gRPC routes answered on
    /// any listener that negotiated HTTP/2 (a public HTTPS port bypassed the loopback bind, the TLS
    /// requirement and <see cref="GrpcConnectionLimiter"/>), and the dedicated gRPC port served the
    /// whole host pipeline (<c>/graphql</c>, OData, S3, …) past the posture its options declare.
    ///
    /// <para>The listener is identified by the connection's local port — the fact Kestrel records
    /// for the socket that accepted the request — never by the <c>Host</c> header, which the client
    /// writes (so <c>RequireHost</c> is not a boundary). Two halves:</para>
    /// <list type="bullet">
    /// <item><see cref="StartupFilter"/> runs first in the host pipeline: on the gRPC port, a path
    /// outside the gRPC services is a 404 before any host middleware runs.</item>
    /// <item><see cref="GuardGrpcEndpoints"/> wraps every mapped gRPC endpoint: on any other port
    /// it is a 404, so the method set is unreachable there.</item>
    /// </list>
    /// </summary>
    internal static class GrpcListenerIsolation
    {
        private const string ReflectionServicePrefix = "/grpc.reflection.v1alpha.ServerReflection/";

        public static bool ArrivedOnGrpcListener(HttpContext http, GrpcWireOptions options)
            => http.Connection.LocalPort == options.Port;

        /// <summary>
        /// Wraps <paramref name="builder"/>'s endpoints (methods and the host's unimplemented
        /// catch-alls alike) so they answer only on the gRPC listener.
        /// </summary>
        public static void GuardGrpcEndpoints(IEndpointConventionBuilder builder, GrpcWireOptions options)
        {
            builder.Add(endpoint =>
            {
                var inner = endpoint.RequestDelegate
                    ?? throw new InvalidOperationException("gRPC endpoint has no request delegate to guard.");
                endpoint.RequestDelegate = http =>
                {
                    if (!ArrivedOnGrpcListener(http, options))
                    {
                        http.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    }
                    return inner(http);
                };
            });
        }

        /// <summary>
        /// Outermost host middleware: confines the gRPC port to the gRPC service paths. Registered
        /// as an <see cref="IStartupFilter"/> so it runs before anything the host's own
        /// <c>Configure</c> adds, whatever order the host wires its middleware in (invariant 16).
        /// </summary>
        internal sealed class StartupFilter : IStartupFilter
        {
            private readonly GrpcWireOptions _options;
            private readonly GrpcContractProvider _contracts;

            public StartupFilter(GrpcWireOptions options, GrpcContractProvider contracts)
            {
                _options = options;
                _contracts = contracts;
            }

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                var servicePrefix = $"/{_contracts.ServiceFullName}/";
                app.Use(async (http, nextMiddleware) =>
                {
                    if (ArrivedOnGrpcListener(http, _options) && !IsGrpcServicePath(http.Request.Path, servicePrefix))
                    {
                        http.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    await nextMiddleware(http);
                });
                next(app);
            };

            private static bool IsGrpcServicePath(PathString path, string servicePrefix)
                => path.Value is { } value
                   && (value.StartsWith(servicePrefix, StringComparison.Ordinal)
                       || value.StartsWith(ReflectionServicePrefix, StringComparison.Ordinal));
        }
    }
}

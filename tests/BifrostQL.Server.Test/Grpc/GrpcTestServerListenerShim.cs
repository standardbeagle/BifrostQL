using BifrostQL.Server.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// TestServer has no sockets, so every request reports local port 0 and the gRPC listener
    /// isolation (which keys on the accepting socket's port) would refuse it. This shim stamps the
    /// configured gRPC port onto each request as if the dedicated listener had accepted it. Register
    /// it BEFORE <c>AddBifrostGrpc</c>: the first-registered startup filter runs outermost, so the
    /// production isolation middleware and endpoint guard then see a gRPC-listener request and run
    /// exactly as in production. Real-socket facts (GrpcListenerIsolationTests) never use it.
    /// </summary>
    internal sealed class GrpcTestServerListenerShim : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var port = app.ApplicationServices.GetRequiredService<GrpcWireOptions>().Port;
            app.Use((http, nextMiddleware) =>
            {
                http.Connection.LocalPort = port;
                return nextMiddleware(http);
            });
            next(app);
        };
    }
}

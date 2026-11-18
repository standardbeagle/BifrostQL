using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Keeps the host's hosting URLs bound once the gRPC listener is added. The gRPC listener is
    /// declared in code (<see cref="KestrelServerOptions.Listen(IPAddress, int, Action{ListenOptions})"/>),
    /// and Kestrel treats ANY code-declared endpoint as replacing the URLs from <c>UseUrls</c>,
    /// <c>ASPNETCORE_URLS</c> or <c>--urls</c> ("Overriding address(es)..."), so registering the
    /// adapter silently unbound the host's own HTTP port.
    ///
    /// <para>The hosting layer copies those URLs into <see cref="IServerAddressesFeature"/> before
    /// it builds the pipeline, and Kestrel binds only at start, so this startup filter turns each
    /// URL into a code-declared listener next to the gRPC one and clears the list. Endpoints from
    /// the <c>Kestrel:Endpoints</c> configuration section are already additive and are untouched.
    /// With <see cref="KestrelServerOptions"/>' <c>PreferHostingUrls</c> set, Kestrel binds the
    /// URLs itself, so nothing is carried. A URL Kestrel could not have bound either fails startup
    /// here, which is what Kestrel would have done.</para>
    /// </summary>
    internal sealed class GrpcHostUrlCarryOver : IStartupFilter
    {
        private readonly IOptions<KestrelServerOptions> _kestrel;
        private readonly ILogger<GrpcHostUrlCarryOver> _logger;

        public GrpcHostUrlCarryOver(IOptions<KestrelServerOptions> kestrel, ILogger<GrpcHostUrlCarryOver> logger)
        {
            _kestrel = kestrel;
            _logger = logger;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var addresses = app.ServerFeatures.Get<IServerAddressesFeature>();
            var kestrel = _kestrel.Value;
            if (addresses is not null && !addresses.PreferHostingUrls && !addresses.Addresses.IsReadOnly && addresses.Addresses.Count > 0)
            {
                foreach (var url in addresses.Addresses)
                {
                    Bind(kestrel, BindingAddress.Parse(url));
                    _logger.LogDebug("Hosting URL {Url} carried onto Kestrel beside the gRPC listener.", url);
                }
                addresses.Addresses.Clear();
            }
            next(app);
        };

        private static void Bind(KestrelServerOptions kestrel, BindingAddress address)
        {
            Action<ListenOptions> configure = string.Equals(address.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                ? listen => listen.UseHttps()
                : _ => { };

            if (address.IsNamedPipe)
            {
                kestrel.ListenNamedPipe(address.NamedPipeName, configure);
                return;
            }
            if (address.IsUnixPipe)
            {
                kestrel.ListenUnixSocket(address.UnixPipePath, configure);
                return;
            }
            if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                kestrel.ListenLocalhost(address.Port, configure);
                return;
            }
            if (IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip))
            {
                kestrel.Listen(ip, address.Port, configure);
                return;
            }
            // "*", "+", or a host name: Kestrel binds every interface for these, as it would have.
            kestrel.ListenAnyIP(address.Port, configure);
        }
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace BifrostQL.Server
{
    /// <summary>
    /// Options for configuring HTTP/3 support in BifrostQL's Kestrel server.
    /// HTTP/3 uses QUIC transport, providing benefits over HTTP/2:
    /// - Independent streams eliminate head-of-line blocking
    /// - 0-RTT connection establishment via TLS 1.3 reduces reconnect latency
    /// - Connection migration allows seamless IP changes (mobile networks)
    ///
    /// Protocol fallback is automatic via ALPN negotiation:
    /// HTTP/3 (QUIC) -> HTTP/2 (TCP+TLS) -> HTTP/1.1 (TCP+TLS)
    /// </summary>
    public sealed class BifrostHttp3Options
    {
        /// <summary>
        /// The HTTPS port to listen on. Defaults to 5001.
        /// HTTP/3 requires TLS, so only HTTPS endpoints support HTTP/3.
        /// </summary>
        public int HttpsPort { get; set; } = 5001;

        /// <summary>
        /// Optional HTTP port for plaintext traffic (HTTP/1.1 and HTTP/2 only).
        /// Set to null to disable plaintext HTTP. Defaults to 5000.
        /// </summary>
        public int? HttpPort { get; set; } = 5000;

        /// <summary>
        /// The IP address to bind to. Defaults to <see cref="IPAddress.Any"/>.
        /// </summary>
        public IPAddress ListenAddress { get; set; } = IPAddress.Any;
    }

    public static class BifrostHttp3Extensions
    {
        /// <summary>
        /// Configures Kestrel to support HTTP/3 alongside HTTP/1.1 and HTTP/2.
        /// HTTP/3 runs over QUIC, eliminating TCP head-of-line blocking and enabling
        /// 0-RTT reconnection and connection migration.
        ///
        /// The HTTPS endpoint is configured with <see cref="HttpProtocols.Http1AndHttp2AndHttp3"/>
        /// so clients can negotiate the best available protocol via ALPN.
        /// An optional HTTP endpoint serves HTTP/1.1 and HTTP/2 traffic without TLS.
        ///
        /// Requires a valid TLS certificate (development certificate or configured cert).
        /// </summary>
        /// <param name="builder">The web application builder.</param>
        /// <param name="configure">Optional configuration for ports and listen address.</param>
        /// <returns>The builder for chaining.</returns>
        public static WebApplicationBuilder UseBifrostHttp3(
            this WebApplicationBuilder builder,
            Action<BifrostHttp3Options>? configure = null)
        {
            var options = new BifrostHttp3Options();
            configure?.Invoke(options);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                ConfigureKestrelForHttp3(kestrel, options);
            });

            return builder;
        }

        /// <summary>
        /// Configures Kestrel server options for HTTP/3 support.
        /// Separated for testability - the Kestrel configuration callback can be validated
        /// without spinning up a full web host.
        /// </summary>
        internal static void ConfigureKestrelForHttp3(KestrelServerOptions kestrel, BifrostHttp3Options options)
        {
            // HTTPS endpoint: HTTP/1.1 + HTTP/2 + HTTP/3
            // Clients negotiate via ALPN (TLS) for HTTP/2 and Alt-Svc header for HTTP/3
            kestrel.Listen(options.ListenAddress, options.HttpsPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                listenOptions.UseHttps();
            });

            // Optional plaintext HTTP endpoint: HTTP/1.1 + HTTP/2 (no HTTP/3 without TLS)
            if (options.HttpPort.HasValue)
            {
                kestrel.Listen(options.ListenAddress, options.HttpPort.Value, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                });
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The process-stable HMAC key (and TTL) List page tokens are signed and validated with. Resolved
    /// ONCE at startup as a singleton — a per-call random key would make every issued token fail its
    /// own validation. Mirrors the OData middleware's key resolution: a configured
    /// <see cref="GrpcWireOptions.PageTokenSecret"/> is used verbatim; absent one, a per-instance
    /// random key is generated and the trade-off (tokens do not survive a restart or resolve on
    /// another instance) is logged, never silent.
    /// </summary>
    internal sealed class GrpcPageTokenKey
    {
        public byte[] Secret { get; }
        public TimeSpan Ttl { get; }

        private GrpcPageTokenKey(byte[] secret, TimeSpan ttl)
        {
            Secret = secret;
            Ttl = ttl;
        }

        public static GrpcPageTokenKey Resolve(GrpcWireOptions options, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            if (!string.IsNullOrEmpty(options.PageTokenSecret))
                return new GrpcPageTokenKey(Encoding.UTF8.GetBytes(options.PageTokenSecret), options.PageTokenTtl);

            logger.LogWarning(
                "No gRPC PageTokenSecret configured; using a per-instance random key. " +
                "In-flight List page tokens will not survive a restart or resolve on another instance.");
            return new GrpcPageTokenKey(RandomNumberGenerator.GetBytes(32), options.PageTokenTtl);
        }
    }
}

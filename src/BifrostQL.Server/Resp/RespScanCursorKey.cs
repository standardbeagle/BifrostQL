using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// The process-stable HMAC key, lifetime and clock that SCAN cursors are signed and validated
    /// with. Resolved ONCE as a singleton: a per-call random key would make every issued cursor fail
    /// its own validation. Mirrors the gRPC page-token key and the OData continuation-token key —
    /// a configured secret is used verbatim, and absent one a per-instance random key is generated
    /// with the trade-off logged rather than left silent.
    /// </summary>
    internal sealed class RespScanCursorKey
    {
        public byte[] Secret { get; }
        public TimeSpan Ttl { get; }
        public Func<DateTimeOffset> Clock { get; }

        public RespScanCursorKey(byte[] secret, TimeSpan ttl, Func<DateTimeOffset>? clock = null)
        {
            Secret = secret;
            Ttl = ttl;
            Clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public static RespScanCursorKey Resolve(RespWireOptions options, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            if (!string.IsNullOrEmpty(options.ScanCursorSecret))
                return new RespScanCursorKey(
                    Encoding.UTF8.GetBytes(options.ScanCursorSecret), options.ScanCursorTtl);

            logger.LogWarning(
                "No RESP ScanCursorSecret configured; using a per-instance random key. In-flight SCAN "
                + "cursors will not survive a restart or resolve on another instance.");
            return new RespScanCursorKey(RandomNumberGenerator.GetBytes(32), options.ScanCursorTtl);
        }
    }
}

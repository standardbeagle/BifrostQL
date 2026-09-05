using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The one shared seam for "sanitize the wire, keep the detail" (findings H6,
    /// M31). A lookup miss whose caller-supplied name must never reach the client
    /// logs the full detail here and returns the sanitized
    /// <see cref="BifrostExecutionError"/>, so the log/sanitize pair is one call and
    /// cannot drift. Core resolvers and query-model code take no
    /// <see cref="ILogger"/> (several construction paths have no DI at all), so the
    /// sink is a static: hosts wire <see cref="Logger"/> once
    /// (BifrostQL.Server does so at middleware construction); left null, the detail
    /// is dropped exactly as before rather than ever reaching the wire.
    /// </summary>
    public static class BifrostErrorSink
    {
        /// <summary>
        /// Destination for server-side detail. Set once by the host; null means
        /// "no log sink attached" and the sanitized error is returned unchanged.
        /// </summary>
        public static ILogger? Logger { get; set; }

        /// <summary>
        /// Logs <paramref name="detail"/> (which carries the caller-supplied name)
        /// server-side at Information and returns the sanitized
        /// <paramref name="wireMessage"/> as a <see cref="BifrostExecutionError"/>.
        /// <paramref name="site"/> names the surfacing site so the log record
        /// points at the code path, not just the miss.
        /// </summary>
        public static BifrostExecutionError LookupMiss(string wireMessage, string detail, string site)
        {
            Logger?.LogInformation(
                "Client-visible lookup miss at {Site}; sanitized off the wire. Detail: {Detail}",
                site, detail);
            return new BifrostExecutionError(wireMessage);
        }
    }
}

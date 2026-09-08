using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The one shared seam for "sanitize the wire, keep the detail" (findings H6,
    /// M31). A lookup miss whose caller-supplied name must never reach the client
    /// logs the full detail here and returns the sanitized
    /// <see cref="BifrostExecutionError"/>, so the log/sanitize pair is one call and
    /// cannot drift.
    /// </summary>
    /// <remarks>
    /// The sink is a process-wide static, not a DI service, because two of its
    /// callers are constructed with no service access at all: <c>QueryField</c>
    /// resolves tables inside the shared parse task, and the file/generic resolvers
    /// are built by <c>BifrostDispatcher</c> at schema-construction time. The hosts
    /// that do have logging wire <see cref="Logger"/> with <c>??=</c> (the HTTP
    /// middleware and both intent executors), so the semantics are
    /// <b>first writer wins for the life of the process</b>: a second host built in
    /// the same process (multi-host tests, multi-tenant hosting) logs through the
    /// first host's logger and category, and that logger is held after its host is
    /// disposed. Set the property directly to override. Left null, the detail is
    /// dropped exactly as before the seam existed rather than ever reaching the wire.
    /// </remarks>
    public static class BifrostErrorSink
    {
        private static ILogger? _logger;

        /// <summary>
        /// Destination for server-side detail. Null means "no log sink attached"
        /// and the sanitized error is returned unchanged. The write is a single
        /// reference store published with release semantics; the read is acquire,
        /// so a sink set on one thread is visible to lookup misses on every other.
        /// </summary>
        public static ILogger? Logger
        {
            get => Volatile.Read(ref _logger);
            set => Volatile.Write(ref _logger, value);
        }

        internal static IDisposable OverrideForTesting(ILogger logger)
        {
            var previous = Logger;
            Logger = logger;
            return new LoggerScope(previous);
        }

        private sealed class LoggerScope(ILogger? previous) : IDisposable
        {
            public void Dispose() => Logger = previous;
        }

        /// <summary>
        /// Logs <paramref name="detail"/> (which carries the caller-supplied name)
        /// server-side at Debug — the level every other identifier-bearing
        /// diagnostic in Core uses (<c>BulkBatchPlanBuilder</c>, <c>RawSqlExecutor</c>,
        /// SQL detail) — and returns the sanitized <paramref name="wireMessage"/> as a
        /// <see cref="BifrostExecutionError"/>. <paramref name="site"/> names the
        /// surfacing site so the log record points at the code path, not just the miss.
        /// </summary>
        public static BifrostExecutionError LookupMiss(string wireMessage, string detail, string site)
        {
            Logger?.LogDebug(
                "Client-visible lookup miss at {Site}; sanitized off the wire. Detail: {Detail}",
                site, detail);
            return new BifrostExecutionError(wireMessage);
        }
    }
}

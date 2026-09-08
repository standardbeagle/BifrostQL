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
    /// that do have logging wire <see cref="Logger"/> through <see cref="Attach"/>
    /// (the HTTP middleware and both intent executors), so the semantics are
    /// <b>first writer wins for the life of the process</b>: a second host built in
    /// the same process (multi-host tests, multi-tenant hosting) logs through the
    /// first host's logger and category, and that logger is held after its host is
    /// disposed. Set the property directly to override. Left null, the detail is
    /// dropped exactly as before the seam existed rather than ever reaching the wire.
    ///
    /// <para><see cref="Attach"/> exists because "first writer wins" spelled as
    /// <c>Logger ??= …</c> was a non-atomic read-then-write, and the overwhelmingly
    /// common caller builds an executor with NO services — so the candidate is null
    /// and the store writes null. One such constructor racing a real attach could
    /// therefore null out an already-attached logger between its own read and write.
    /// <see cref="Attach"/> closes both halves: a null candidate never writes at all,
    /// and a real one is published with <see cref="Interlocked.CompareExchange{T}"/>
    /// against null, so only the genuine first writer wins and no later
    /// <see cref="Attach"/> can clobber it. Only the <see cref="Logger"/> setter and
    /// <see cref="OverrideForTesting"/> write unconditionally.</para>
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

        /// <summary>
        /// First-writer-wins attach for hosts that have logging. A null
        /// <paramref name="logger"/> (executor built with no services) never writes;
        /// a non-null one is published only if the slot is still null. Every
        /// production attach site routes through here — never <c>Logger ??=</c>.
        /// </summary>
        public static void Attach(ILogger? logger)
        {
            if (logger != null)
                Interlocked.CompareExchange(ref _logger, logger, null);
        }

        /// <summary>
        /// Unconditionally installs <paramref name="logger"/> and restores the prior
        /// value on dispose, so one fact can observe its own sink whatever a host
        /// attached first. Unlike <see cref="Attach"/> this DOES overwrite; it is
        /// <c>internal</c>, which is the <c>InternalsVisibleTo</c> boundary
        /// (Server, the dialect packages, Benchmarks, Core.Test), not a test-only one.
        /// Two scopes on parallel threads overwrite each other; only
        /// <c>TableLookupErrorSinkTests</c> uses it today.
        /// </summary>
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

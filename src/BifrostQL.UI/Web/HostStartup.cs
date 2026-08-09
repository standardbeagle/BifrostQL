using System.Net.Sockets;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// The entry point's start-the-host decision, extracted so the failure branch is
    /// taken BEFORE anything resolves services off the host.
    ///
    /// <c>WebApplication.RunAsync</c> is start-plus-wait wrapped in a <c>using</c>: a
    /// bind failure disposes the host, but the caller only learns about it by awaiting
    /// a task it was not awaiting yet. The desktop shell therefore raced ahead into
    /// <c>DesktopShell.RunAsync</c> and touched <c>app.Environment</c> on the disposed
    /// provider, replacing "port 5000 is in use" with an <c>ObjectDisposedException</c>
    /// stack trace. Splitting start from wait makes the failure a value the caller must
    /// handle, and keeps the real diagnosis in front of the user.
    /// </summary>
    public static class HostStartup
    {
        /// <summary>
        /// Starts <paramref name="app"/>. Returns <c>null</c> when the host is listening
        /// (the provider is live and safe to resolve from), or an actionable operator
        /// message when the port could not be bound — in which case the host has been
        /// disposed and must not be used.
        /// </summary>
        public static async Task<string?> TryStartAsync(WebApplication app, int port, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(app);
            try
            {
                await app.StartAsync(cancellationToken);
                return null;
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                await app.DisposeAsync();
                return DescribeAddressInUse(port);
            }
        }

        /// <summary>
        /// True when <paramref name="ex"/> or anything it wraps is an
        /// "address already in use" socket error. Kestrel surfaces this as
        /// <c>AddressInUseException</c>, an <c>IOException</c>, or an
        /// <c>AggregateException</c> depending on the platform and the number of
        /// configured endpoints, so the whole chain is walked rather than one type
        /// being matched.
        /// </summary>
        public static bool IsAddressInUse(Exception? ex)
        {
            while (ex is not null)
            {
                if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                    return true;
                if (ex is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                        if (IsAddressInUse(inner))
                            return true;
                    return false;
                }
                ex = ex.InnerException;
            }
            return false;
        }

        /// <summary>The message shown to the operator when the port is taken.</summary>
        public static string DescribeAddressInUse(int port) =>
            $"Port {port} is already in use, so the BifrostQL UI server could not start. " +
            $"Another BifrostQL UI instance is the usual cause; any other process listening on " +
            $"{port} will do it too. Close that process, or start on a different port with " +
            $"--port <number> (short form -p).";
    }
}

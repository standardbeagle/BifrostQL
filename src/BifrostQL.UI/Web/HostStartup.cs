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

        /// <summary>
        /// Starts a host on <paramref name="requestedPort"/>; when that port is taken
        /// and the operator did NOT pin it (<paramref name="portWasExplicit"/> false),
        /// rebuilds on port 0 so the OS assigns a free one — the desktop app must open
        /// its window, not die, when something else squats the default port. An
        /// explicitly pinned port stays a hard contract and fails fast with the
        /// actionable message. <c>Port</c> is always the port actually bound (resolved
        /// from the server's address, so the port-0 case reports the real number).
        /// </summary>
        public static async Task<(WebApplication? App, int Port, string? Failure)> StartWithFallbackAsync(
            Func<int, WebApplication> buildApp, int requestedPort, bool portWasExplicit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buildApp);

            var app = buildApp(requestedPort);
            var failure = await TryStartAsync(app, requestedPort, cancellationToken);
            if (failure is null)
                return (app, BoundPort(app, requestedPort), null);
            if (portWasExplicit)
                return (null, requestedPort, failure);

            app = buildApp(0);
            failure = await TryStartAsync(app, 0, cancellationToken);
            return failure is null
                ? (app, BoundPort(app, 0), null)
                : (null, requestedPort, failure);
        }

        /// <summary>
        /// The port the started server actually bound, read from its resolved
        /// addresses — with port 0 the URL the host was built with is useless.
        /// </summary>
        private static int BoundPort(WebApplication app, int requestedPort)
        {
            foreach (var url in app.Urls)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
                    return uri.Port;
            }
            return requestedPort;
        }

        /// <summary>The message shown to the operator when the port is taken.</summary>
        public static string DescribeAddressInUse(int port) =>
            $"Port {port} is already in use, so the BifrostQL UI server could not start. " +
            $"Another BifrostQL UI instance is the usual cause; any other process listening on " +
            $"{port} will do it too. Close that process, or start on a different port with " +
            $"--port <number> (short form -p).";
    }
}

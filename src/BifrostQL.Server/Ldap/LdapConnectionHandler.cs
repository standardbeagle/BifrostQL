using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BifrostQL.Server.Pgwire; // DuplexPipeStream: shared Kestrel IDuplexPipe→Stream glue, not pgwire-specific.

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Kestrel connection handler for the LDAPv3 front door. Slice 2 owns the connection lifecycle
    /// and the message codec; it does not authenticate binds, execute searches, or write. The loop
    /// reads one LDAPMessage, answers it, and repeats, bounded on every axis of an unauthenticated
    /// wire:
    ///
    /// <list type="bullet">
    /// <item>A malformed message closes predictably: the loop sends a Notice of Disconnection and
    /// returns, so a wire violation from a hostile peer never escapes to Kestrel as an unhandled
    /// throw (protocol-adapter-security invariant 1) and never leaves the client hanging
    /// (criterion 4).</item>
    /// <item>Every operation the slice understands but does not yet execute — Bind, Search,
    /// ExtendedRequest — is answered with <c>unwillingToPerform</c>; an unrecognized protocolOp is a
    /// fatal protocol error. The client always gets a reply, never a hang (criterion 4).</item>
    /// <item>An idle connection is closed after <see cref="LdapWireOptions.IdleTimeout"/>; the front
    /// door admits at most <see cref="LdapWireOptions.MaxConnections"/> connections and each
    /// connection at most <see cref="LdapWireOptions.MaxOutstandingOperations"/> in-flight
    /// operations (criteria 2, 3).</item>
    /// </list>
    ///
    /// <para>UnbindRequest and AbandonRequest carry no response by protocol: Unbind closes the
    /// connection, Abandon is a no-op here (no executed operation can be outstanding this slice).</para>
    /// </summary>
    internal sealed class LdapConnectionHandler : ConnectionHandler
    {
        private readonly LdapWireOptions _options;
        private readonly LdapBoundedCounter _connections;
        private readonly LdapBindAuthenticator? _authenticator;
        private readonly ILogger<LdapConnectionHandler> _logger;

        public LdapConnectionHandler(
            LdapWireOptions options,
            LdapBoundedCounter? connectionLimiter = null,
            LdapBindAuthenticator? authenticator = null,
            ILogger<LdapConnectionHandler>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _connections = connectionLimiter ?? new LdapBoundedCounter(options.MaxConnections, "MaxConnections");
            _authenticator = authenticator;
            _logger = logger ?? NullLogger<LdapConnectionHandler>.Instance;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            await using var stream = new DuplexPipeStream(connection.Transport);
            // The remote endpoint is the per-source rate-limit key for bind attempts.
            var source = connection.RemoteEndPoint?.ToString() ?? "unknown";
            await HandleConnectionAsync(stream, connection.ConnectionClosed, source);
        }

        /// <summary>
        /// Drives one connection to completion. Written against a plain <see cref="Stream"/> so it
        /// runs identically over a real socket (tests, production). Admission is refused past the
        /// connection cap; every accepted connection reads/answers messages until Unbind, EOF, a
        /// fatal protocol error, or idle timeout.
        /// </summary>
        internal async Task HandleConnectionAsync(Stream stream, CancellationToken ct, string source = "unknown")
        {
            if (!_connections.TryAcquire())
            {
                _logger.LogWarning("ldap connection refused: at the {Max}-connection cap.", _options.MaxConnections);
                await TrySendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                    LdapResultCode.UnavailableCriticalExtension, "server connection limit reached"), ct);
                return;
            }

            var outstanding = new LdapBoundedCounter(_options.MaxOutstandingOperations, "MaxOutstandingOperations");
            var reader = new LdapMessageReader(
                _options.MaxMessageLength, _options.MaxNestingDepth, _options.MaxFilterComponents, _options.MaxSearchAttributes);
            try
            {
                while (true)
                {
                    LdapRequest? request;
                    try
                    {
                        request = await ReadWithIdleTimeoutAsync(reader, stream, ct);
                    }
                    catch (Exception ex) when (ex is LdapProtocolException or FormatException or OverflowException or ArgumentException)
                    {
                        // Malformed wire input from a (possibly unauthenticated) peer. Only the adapter's
                        // own curated protocol-exception text is client-safe; a BCL parse fault sanitizes
                        // to a generic string (never forward internal detail). Answer a Notice of
                        // Disconnection, then close — the client learns the reason instead of only an EOF.
                        var detail = ex is LdapProtocolException ? ex.Message : "malformed BER";
                        _logger.LogDebug(ex, "ldap protocol error; closing connection: {Detail}", detail);
                        await TrySendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                            LdapResultCode.ProtocolError, detail), ct);
                        return;
                    }

                    if (request is null)
                        return; // clean EOF: peer closed

                    if (!outstanding.TryAcquire())
                    {
                        _logger.LogWarning("ldap connection exceeded the {Max}-outstanding-operation cap; closing.",
                            _options.MaxOutstandingOperations);
                        await TrySendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                            LdapResultCode.UnwillingToPerform, "too many outstanding operations"), ct);
                        return;
                    }
                    try
                    {
                        if (!await DispatchAsync(stream, request, source, ct))
                            return; // Unbind / fatal op: close the connection
                    }
                    finally
                    {
                        outstanding.Release();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "ldap connection ended: {Reason}", ex.Message);
            }
            finally
            {
                _connections.Release();
            }
        }

        /// <summary>
        /// Dispatches one decoded request, returning whether the connection stays open. Bind, Search,
        /// and ExtendedRequest are answered with the protocol-appropriate <c>unwillingToPerform</c>
        /// result (their execution is a non-goal this slice); Unbind closes; Abandon is a no-op; an
        /// unrecognized protocolOp is a fatal protocol error that closes the connection.
        /// </summary>
        private async Task<bool> DispatchAsync(Stream stream, LdapRequest request, string source, CancellationToken ct)
        {
            switch (request.Operation)
            {
                case LdapUnbindRequest:
                    return false; // no response; the client is closing the connection

                case LdapAbandonRequest:
                    // No executed operation can be outstanding this slice, so there is nothing to
                    // cancel. Abandon carries no response by protocol — acknowledge by continuing.
                    return true;

                case LdapBindRequest bind:
                    await HandleBindAsync(stream, request.MessageId, bind, source, ct);
                    return true; // a bind (success or failure) leaves the connection open for retry

                case LdapSearchRequest:
                    await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                        request.MessageId, LdapResultCode.UnwillingToPerform,
                        "search is not enabled on this listener"), ct);
                    return true;

                case LdapExtendedRequest extended:
                    // StartTLS and every other extended op are refused (TLS/SASL are non-goals).
                    await SendAsync(stream, LdapMessageWriter.ExtendedResponse(
                        request.MessageId, LdapResultCode.UnwillingToPerform,
                        $"extended operation '{extended.RequestName}' is not supported"), ct);
                    return true;

                default:
                    // An application tag the codec does not model: a fatal protocol error. Send the
                    // unsolicited Notice of Disconnection and close rather than guess a response shape.
                    await SendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                        LdapResultCode.ProtocolError, $"unsupported protocolOp 0x{request.ProtocolOpTag:X2}"), ct);
                    return false;
            }
        }

        /// <summary>
        /// Authenticates a BindRequest through the configured <see cref="LdapBindAuthenticator"/> and
        /// answers a BindResponse. When no authenticator is registered the listener is fail-closed for
        /// authentication: bind is refused with <c>unwillingToPerform</c> (there is no default ambient
        /// credential store — criterion 1). Every authenticated failure class returns the SAME uniform
        /// <c>invalidCredentials</c> the authenticator produces; the connection stays open for retry
        /// (bounded by the per-source / per-account rate limits).
        /// </summary>
        private async Task HandleBindAsync(Stream stream, int messageId, LdapBindRequest bind, string source, CancellationToken ct)
        {
            if (_authenticator is null)
            {
                await SendAsync(stream, LdapMessageWriter.BindResponse(
                    messageId, LdapResultCode.UnwillingToPerform,
                    "bind authentication is not enabled on this listener"), ct);
                return;
            }

            var result = await _authenticator.AuthenticateAsync(bind, source, ct);
            await SendAsync(stream, LdapMessageWriter.BindResponse(messageId, result.ResultCode, result.DiagnosticMessage), ct);
        }

        // Reads the next message, closing the connection if it stays idle past the configured
        // timeout. A read cancelled by the idle deadline (not the outer connection token) surfaces as
        // a clean end-of-connection, not a protocol error.
        private async Task<LdapRequest?> ReadWithIdleTimeoutAsync(LdapMessageReader reader, Stream stream, CancellationToken ct)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_options.IdleTimeout);
            try
            {
                return await reader.ReadRequestAsync(stream, idle.Token);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("ldap connection idle past {Timeout}; closing.", _options.IdleTimeout);
                return null; // treat idle timeout as a clean close
            }
        }

        private static async Task SendAsync(Stream stream, byte[] message, CancellationToken ct)
        {
            await stream.WriteAsync(message, ct);
            await stream.FlushAsync(ct);
        }

        // Best-effort send on a path where the socket may already be gone (refusal / fatal close):
        // a write failure there must never mask the reason we are closing.
        private static async Task TrySendAsync(Stream stream, byte[] message, CancellationToken ct)
        {
            try { await SendAsync(stream, message, ct); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}

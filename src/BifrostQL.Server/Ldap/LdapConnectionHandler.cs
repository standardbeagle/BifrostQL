using System.Security.Cryptography;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BifrostQL.Core.Model;
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
        ///
        /// <para><paramref name="tlsEstablished"/> declares that <paramref name="stream"/> is already
        /// confidential — an LDAPS connection whose handshake completed before the first byte of LDAP
        /// was read. It only ever moves in that direction: nothing in the loop returns a session to
        /// cleartext.</para>
        /// </summary>
        internal async Task HandleConnectionAsync(
            Stream stream, CancellationToken ct, string source = "unknown", bool tlsEstablished = false)
        {
            if (!_connections.TryAcquire())
            {
                _logger.LogWarning("ldap connection refused: at the {Max}-connection cap.", _options.MaxConnections);
                await TrySendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                    LdapResultCode.UnavailableCriticalExtension, "server connection limit reached"), ct);
                return;
            }

            var outstanding = new LdapBoundedCounter(_options.MaxOutstandingOperations, "MaxOutstandingOperations");
            // Read through a buffer so the framing reader costs one socket read per burst instead of
            // one per byte — and so anything the peer PIPELINED behind the current message is visible
            // to this process rather than sitting unseen in the kernel (see LdapBufferedStream).
            var wire = new LdapBufferedStream(stream);
            var reader = new LdapMessageReader(
                _options.MaxMessageLength, _options.MaxNestingDepth, _options.MaxFilterComponents, _options.MaxSearchAttributes);
            // Session state for THIS connection: whether a bind has authenticated it, and whether that
            // bind was anonymous (an anonymous session is limited to the RootDSE/subschema — criterion 4).
            var session = new LdapSessionState { TlsEstablished = tlsEstablished };
            // Pre-auth deadline: the admission slot was taken at accept, so an unauthenticated peer
            // must not be able to hold it past this even while sending traffic (failing binds keep the
            // connection non-idle, so the idle timeout alone does not reclaim the slot).
            var preAuthDeadline = DateTimeOffset.UtcNow + _options.AuthenticationTimeout;
            try
            {
                while (true)
                {
                    var readTimeout = _options.IdleTimeout;
                    if (!session.Authenticated)
                    {
                        var remaining = preAuthDeadline - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                        {
                            _logger.LogDebug("ldap connection did not authenticate within {Timeout}; closing.",
                                _options.AuthenticationTimeout);
                            return;
                        }
                        if (remaining < readTimeout)
                            readTimeout = remaining;
                    }

                    LdapRequest? request;
                    try
                    {
                        request = await ReadWithDeadlineAsync(reader, wire, readTimeout, ct);
                    }
                    catch (Exception ex) when (ex is LdapProtocolException or FormatException or OverflowException or ArgumentException)
                    {
                        // Malformed wire input from a (possibly unauthenticated) peer. Only the adapter's
                        // own curated protocol-exception text is client-safe; a BCL parse fault sanitizes
                        // to a generic string (never forward internal detail). Answer a Notice of
                        // Disconnection, then close — the client learns the reason instead of only an EOF.
                        var detail = ex is LdapProtocolException ? ex.Message : "malformed BER";
                        _logger.LogDebug(ex, "ldap protocol error; closing connection: {Detail}", detail);
                        await TrySendAsync(wire, LdapMessageWriter.NoticeOfDisconnection(
                            LdapResultCode.ProtocolError, detail), ct);
                        return;
                    }

                    if (request is null)
                        return; // clean EOF: peer closed

                    if (!outstanding.TryAcquire())
                    {
                        _logger.LogWarning("ldap connection exceeded the {Max}-outstanding-operation cap; closing.",
                            _options.MaxOutstandingOperations);
                        await TrySendAsync(wire, LdapMessageWriter.NoticeOfDisconnection(
                            LdapResultCode.UnwillingToPerform, "too many outstanding operations"), ct);
                        return;
                    }
                    try
                    {
                        if (!await DispatchAsync(wire, request, session, source, ct))
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
        private async Task<bool> DispatchAsync(
            Stream stream, LdapRequest request, LdapSessionState session, string source, CancellationToken ct)
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
                    await HandleBindAsync(stream, request.MessageId, bind, session, source, ct);
                    return true; // a bind (success or failure) leaves the connection open for retry

                case LdapSearchRequest search:
                    // Criterion 4: an admitted anonymous session may read only the RootDSE and the
                    // subschema. Anything else is refused for lack of rights BEFORE any search
                    // execution exists, so the later search slice inherits the restriction instead of
                    // having to remember it.
                    if (session.IsAnonymous && !IsDiscoveryBase(search.BaseObject))
                    {
                        await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                            request.MessageId, LdapResultCode.InsufficientAccessRights,
                            "anonymous access is limited to the RootDSE and the subschema"), ct);
                        return true;
                    }
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
        /// answers a BindResponse. The transport gate runs first: a credentialed bind is refused with
        /// <c>confidentialityRequired</c> unless the connection is confidential. When no authenticator is registered the listener is fail-closed for
        /// authentication: bind is refused with <c>unwillingToPerform</c> (there is no default ambient
        /// credential store — criterion 1). Every authenticated failure class returns the SAME uniform
        /// <c>invalidCredentials</c> the authenticator produces; the connection stays open for retry
        /// (bounded by the per-source / per-account rate limits).
        /// </summary>
        private async Task HandleBindAsync(
            Stream stream, int messageId, LdapBindRequest bind, LdapSessionState session, string source, CancellationToken ct)
        {
            // TRANSPORT GATE — deliberately the FIRST statement of the bind path, ahead of the
            // authenticator, the rate limiter, the credential store and the hasher. A credentialed
            // bind on a cleartext connection is refused without the presented credential being
            // resolved, compared, or used to select a code path, so the secret is never acted on
            // where a passive observer already has it. The password bytes the decoder captured are
            // zeroed here rather than carried further (secret hygiene).
            //
            // An ANONYMOUS bind is exempt: it carries no secret to protect. The refusal talks about
            // the transport only — identical for a real DN and a fabricated one — so it cannot become
            // an account-enumeration oracle, and it leaves the connection open so the client can
            // StartTLS and retry. There is no path that infers "cleartext is fine" from a missing
            // certificate: only the explicit development override opens one.
            if (!bind.IsAnonymous && !session.TlsEstablished && !_options.AllowInsecureSimpleBind)
            {
                if (bind.SimplePassword is { Length: > 0 })
                    CryptographicOperations.ZeroMemory(bind.SimplePassword);
                _logger.LogWarning(
                    "ldap credentialed bind from {Source} refused: the connection is not confidential. "
                    + "Use LDAPS or StartTLS.", source);
                await SendAsync(stream, LdapMessageWriter.BindResponse(
                    messageId, LdapResultCode.ConfidentialityRequired,
                    "a credentialed bind requires a confidential transport (LDAPS or StartTLS)"), ct);
                return;
            }

            if (_authenticator is null)
            {
                await SendAsync(stream, LdapMessageWriter.BindResponse(
                    messageId, LdapResultCode.UnwillingToPerform,
                    "bind authentication is not enabled on this listener"), ct);
                return;
            }

            var result = await _authenticator.AuthenticateAsync(bind, source, ct);
            // RFC 4513: a failed bind leaves the session UNAUTHENTICATED — it never downgrades an
            // already-authenticated session to anonymous, and it never authenticates one.
            if (result.Succeeded)
            {
                session.Authenticated = true;
                session.IsAnonymous = result.IsAnonymous;
                session.UserContext = result.UserContext;
            }
            await SendAsync(stream, LdapMessageWriter.BindResponse(messageId, result.ResultCode, result.DiagnosticMessage), ct);
        }

        /// <summary>
        /// Whether a search base is part of the anonymous discovery surface: the RootDSE (the empty
        /// DN) or the subschema subentry. Everything else is directory data.
        /// </summary>
        private static bool IsDiscoveryBase(string baseObject) =>
            baseObject.Length == 0
            || string.Equals(baseObject, LdapDirectoryModel.SubschemaSubentryDn, StringComparison.OrdinalIgnoreCase);

        // Reads the next message, closing the connection if nothing arrives before <paramref
        // name="deadline"/> — the idle timeout, or the shorter remaining pre-auth deadline while the
        // session is unauthenticated. A read cancelled by that deadline (not the outer connection
        // token) surfaces as a clean end-of-connection, not a protocol error.
        private async Task<LdapRequest?> ReadWithDeadlineAsync(
            LdapMessageReader reader, Stream stream, TimeSpan deadline, CancellationToken ct)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(deadline);
            try
            {
                return await reader.ReadRequestAsync(stream, idle.Token);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("ldap connection reached its {Deadline} read deadline; closing.", deadline);
                return null; // treat a read deadline as a clean close
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

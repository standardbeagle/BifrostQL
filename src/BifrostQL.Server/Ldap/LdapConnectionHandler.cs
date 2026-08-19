using System.Security.Cryptography;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BifrostQL.Core.Model;
using BifrostQL.Server.Pgwire; // DuplexPipeStream: shared Kestrel IDuplexPipe→Stream glue, not pgwire-specific.

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Kestrel connection handler for the LDAPv3 front door: the connection lifecycle, the message
    /// codec, bind authentication, search execution, and the StartTLS transport upgrade. There is
    /// no write verb — add, modify, delete and modifyDN are non-goals. The loop reads one
    /// LDAPMessage, answers it, and repeats, bounded on every axis of an unauthenticated wire:
    ///
    /// <list type="bullet">
    /// <item>A malformed message closes predictably: the loop sends a Notice of Disconnection and
    /// returns, so a wire violation from a hostile peer never escapes to Kestrel as an unhandled
    /// throw (protocol-adapter-security invariant 1) and never leaves the client hanging
    /// (criterion 4).</item>
    /// <item>Every operation is answered, including the ones this front door declines: an extended
    /// operation other than StartTLS, and a bind or search on a listener registered without the
    /// seams that would serve it, are answered with <c>unwillingToPerform</c>; an unrecognized
    /// protocolOp is a fatal protocol error. The client always gets a reply, never a hang
    /// (criterion 4).</item>
    /// <item>An idle connection is closed after <see cref="LdapWireOptions.IdleTimeout"/>; the front
    /// door admits at most <see cref="LdapWireOptions.MaxConnections"/> connections and each
    /// connection at most <see cref="LdapWireOptions.MaxOutstandingOperations"/> in-flight
    /// operations (criteria 2, 3).</item>
    /// <item>A credential is never accepted on a cleartext transport: a credentialed bind is
    /// refused with <c>confidentialityRequired</c> before the presented secret is read, unless the
    /// connection is confidential (LDAPS, or a completed StartTLS).</item>
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
        private readonly LdapTlsProvider? _tls;
        private readonly LdapSearchExecutor? _search;
        private readonly ILogger<LdapConnectionHandler> _logger;

        public LdapConnectionHandler(
            LdapWireOptions options,
            LdapBoundedCounter? connectionLimiter = null,
            LdapBindAuthenticator? authenticator = null,
            LdapTlsProvider? tls = null,
            LdapSearchExecutor? search = null,
            ILogger<LdapConnectionHandler>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _connections = connectionLimiter ?? new LdapBoundedCounter(options.MaxConnections, "MaxConnections");
            _authenticator = authenticator;
            _tls = tls;
            _search = search;
            _logger = logger ?? NullLogger<LdapConnectionHandler>.Instance;
        }

        /// <summary>
        /// The per-source rate-limit key. It must identify the CLIENT, not the connection: an
        /// <see cref="System.Net.IPEndPoint"/>'s <c>ToString()</c> is "ip:port" with an EPHEMERAL
        /// port, so keying on the whole endpoint would make the per-source cap per-connection —
        /// a peer opening a fresh connection per bind would evade the cap AND multiply the
        /// rate-limiter's tracked keys. Key on the IP address alone.
        /// </summary>
        internal static string SourceKey(System.Net.EndPoint? remote) => remote switch
        {
            System.Net.IPEndPoint ip => ip.Address.ToString(),
            { } other => other.ToString() ?? "unknown",
            null => "unknown",
        };

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            await using var stream = new DuplexPipeStream(connection.Transport);
            var source = SourceKey(connection.RemoteEndPoint);
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
            try
            {
                await RunSessionAsync(stream, ct, source, tlsEstablished);
            }
            finally
            {
                _connections.Release();
            }
        }

        /// <summary>
        /// Runs the message loop of ONE admitted connection: read a message, answer it, repeat, until
        /// Unbind, EOF, a fatal protocol error, or a deadline. The caller owns admission — the LDAPS
        /// listener takes its slot before the TLS handshake, which is earlier than this method could —
        /// and owns the transport's lifetime.
        /// </summary>
        internal async Task RunSessionAsync(Stream stream, CancellationToken ct, string source, bool tlsEstablished)
        {
            var outstanding = new LdapBoundedCounter(_options.MaxOutstandingOperations, "MaxOutstandingOperations");
            // Read through a buffer so the framing reader costs one socket read per burst instead of
            // one per byte — and so anything the peer PIPELINED behind the current message is visible
            // to this process rather than sitting unseen in the kernel (see LdapBufferedStream).
            var wire = new LdapBufferedStream(stream);
            var reader = NewReader();
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
                        var dispatch = await DispatchAsync(wire, request, session, source, ct);
                        if (!dispatch.KeepOpen)
                            return; // Unbind / fatal op: close the connection
                        if (dispatch.Upgraded is { } upgraded)
                        {
                            // StartTLS completed. Everything after this reads and writes through the
                            // confidential stream, framed by a FRESH reader over a FRESH buffer, so no
                            // byte and no decoder state can survive the upgrade (RFC 4511 §4.14.3.1).
                            wire = upgraded;
                            reader = NewReader();
                        }
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
        }

        /// <summary>
        /// Dispatches one decoded request, returning whether the connection stays open. Bind, Search,
        /// and ExtendedRequest are answered with the protocol-appropriate <c>unwillingToPerform</c>
        /// result (their execution is a non-goal this slice); Unbind closes; Abandon is a no-op; an
        /// unrecognized protocolOp is a fatal protocol error that closes the connection.
        /// </summary>
        private async Task<LdapDispatch> DispatchAsync(
            LdapBufferedStream stream, LdapRequest request, LdapSessionState session, string source, CancellationToken ct)
        {
            switch (request.Operation)
            {
                case LdapUnbindRequest:
                    return LdapDispatch.Close; // no response; the client is closing the connection

                case LdapAbandonRequest:
                    // The loop answers one request at a time, so by the time an Abandon is decoded
                    // the operation it names has already completed — there is nothing left to
                    // cancel. Cancellation of a search that IS in flight comes from the connection
                    // token and the search's own deadline, both of which the executor links; a
                    // client that drops the connection stops the work rather than leaving it
                    // running against the database. Abandon carries no response by protocol.
                    return LdapDispatch.Continue;

                case LdapBindRequest bind:
                    await HandleBindAsync(stream, request.MessageId, bind, session, source, ct);
                    return LdapDispatch.Continue; // a bind (success or failure) leaves the connection open for retry

                case LdapSearchRequest search:
                    await HandleSearchAsync(stream, request, search, session, ct);
                    return LdapDispatch.Continue;

                case LdapExtendedRequest { RequestName: LdapProtocol.StartTlsOid }:
                    return await HandleStartTlsAsync(stream, request.MessageId, session, ct);

                case LdapExtendedRequest extended:
                    // Every other extended op is refused (SASL and the rest are non-goals). The name is
                    // echoed from the caller's own request, so nothing internal reaches the wire.
                    await SendAsync(stream, LdapMessageWriter.ExtendedResponse(
                        request.MessageId, LdapResultCode.UnwillingToPerform,
                        $"extended operation '{extended.RequestName}' is not supported"), ct);
                    return LdapDispatch.Continue;

                default:
                    // An application tag the codec does not model: a fatal protocol error. Send the
                    // unsolicited Notice of Disconnection and close rather than guess a response shape.
                    await SendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                        LdapResultCode.ProtocolError, $"unsupported protocolOp 0x{request.ProtocolOpTag:X2}"), ct);
                    return LdapDispatch.Close;
            }
        }

        /// <summary>
        /// Answers a SearchRequest: zero or more SearchResultEntry messages, then exactly one
        /// SearchResultDone. The Done is sent on EVERY path — success, refusal, or fault — so a
        /// client is never left waiting on a search that has already finished.
        ///
        /// <list type="bullet">
        /// <item><b>Unauthenticated</b> — refused. A bind establishes the identity every read is
        /// scoped by, so a search before one has no scope to narrow from and is never executed.</item>
        /// <item><b>Anonymous</b> — limited to the RootDSE and the subschema. The check runs here
        /// AND in the executor: this one keeps an unmapped base from reaching the model at all, and
        /// the executor's keeps the restriction attached to execution rather than to one call site.</item>
        /// <item><b>No search executor configured</b> — <c>unwillingToPerform</c>. There is no
        /// ambient read path; a listener registered without one serves no data.</item>
        /// </list>
        /// </summary>
        private async Task HandleSearchAsync(
            LdapBufferedStream stream,
            LdapRequest request,
            LdapSearchRequest search,
            LdapSessionState session,
            CancellationToken ct)
        {
            if (!session.Authenticated)
            {
                await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                    request.MessageId, LdapResultCode.InsufficientAccessRights,
                    "bind before searching"), ct);
                return;
            }

            if (session.IsAnonymous && !IsDiscoveryBase(search.BaseObject))
            {
                await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                    request.MessageId, LdapResultCode.InsufficientAccessRights,
                    "anonymous access is limited to the RootDSE and the subschema"), ct);
                return;
            }

            if (_search is null)
            {
                await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                    request.MessageId, LdapResultCode.UnwillingToPerform,
                    "search is not enabled on this listener"), ct);
                return;
            }

            LdapSearchOutcome outcome;
            try
            {
                outcome = await _search.ExecuteAsync(search, request.Controls, session, ct);
            }
            catch (Exception ex) when (ex is LdapProtocolException or FormatException or OverflowException or ArgumentException)
            {
                // A malformed REQUEST CONTROL. The message itself framed correctly — the reader
                // consumed it whole — so the session is still in sync and this is a fault of ONE
                // operation, not a reason to tear the connection down. Answering it here is what
                // keeps this op class symmetric with its siblings (Bind answers a BindResponse,
                // StartTLS an ExtendedResponse) and keeps the promise above: exactly one
                // SearchResultDone on every path (protocol-adapter-security invariants 9 and 10).
                //
                // Nothing has been written for this operation yet — the executor returns a whole
                // outcome before the first entry goes out — so the Done is still the only message
                // this search produces. Only the adapter's OWN curated protocol text reaches the
                // wire; a BCL parse fault sanitizes to a generic string (invariant 3).
                var detail = ex is LdapProtocolException ? ex.Message : "malformed request control";
                _logger.LogDebug(ex, "ldap search refused: {Detail}", detail);
                await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                    request.MessageId, LdapResultCode.ProtocolError, detail), ct);
                return;
            }

            foreach (var entry in outcome.Entries)
                await SendAsync(stream, LdapMessageWriter.SearchResultEntry(request.MessageId, entry), ct);

            await SendAsync(stream, LdapMessageWriter.SearchResultDone(
                request.MessageId, outcome.ResultCode, outcome.Diagnostic, outcome.ResponseControls), ct);
        }

        /// <summary>
        /// Handles the StartTLS extended operation (RFC 4511 §4.14, RFC 4513 §3.1). The pre-bind,
        /// not-yet-confidential state is the ONLY one that negotiates; every other state is refused
        /// with the session left exactly as it was, so there is no sequence of requests that reaches
        /// a mixed or downgraded transport:
        ///
        /// <list type="bullet">
        /// <item><b>No certificate configured</b> — <c>unavailable</c>. The listener stays cleartext
        /// and credentialed binds stay refused; it never pretends to have upgraded.</item>
        /// <item><b>Already confidential</b> (LDAPS, or a second StartTLS) — <c>operationsError</c>,
        /// existing TLS untouched. Renegotiating would mean tearing down a working confidential
        /// stream on an unauthenticated peer's request.</item>
        /// <item><b>Already bound</b> — <c>operationsError</c>. RFC 4513 §3.1.1 requires the
        /// authentication state to be reconsidered when TLS is installed under an existing
        /// association; refusing outright is the state-confusion-free reading of that, and costs a
        /// correct client nothing (StartTLS belongs before bind).</item>
        /// <item><b>Pipelined plaintext</b> — anything the peer sent behind the StartTLS request is a
        /// fatal protocol error, before the success response and before any handshake. Those bytes
        /// were written in the clear on the assumption they would be processed; carrying them across
        /// the upgrade would let an attacker inject cleartext requests that a client believes were
        /// protected (the "plaintext command injection" shape). The connection closes.</item>
        /// </list>
        ///
        /// <para>On the negotiating path the success response is sent FIRST (the client cannot know
        /// to start a handshake otherwise), then the handshake runs under its own deadline. A failed
        /// handshake CLOSES the connection: there is no path back to cleartext, and nothing further
        /// is written, since the peer is mid-TLS and cannot read an LDAP message.</para>
        /// </summary>
        private async Task<LdapDispatch> HandleStartTlsAsync(
            LdapBufferedStream stream, int messageId, LdapSessionState session, CancellationToken ct)
        {
            if (_tls is null)
            {
                await SendAsync(stream, LdapMessageWriter.ExtendedResponse(
                    messageId, LdapResultCode.Unavailable,
                    "StartTLS is not available on this listener"), ct);
                return LdapDispatch.Continue;
            }

            if (session.TlsEstablished)
            {
                await SendAsync(stream, LdapMessageWriter.ExtendedResponse(
                    messageId, LdapResultCode.OperationsError,
                    "TLS is already established on this connection"), ct);
                return LdapDispatch.Continue;
            }

            if (session.Authenticated)
            {
                await SendAsync(stream, LdapMessageWriter.ExtendedResponse(
                    messageId, LdapResultCode.OperationsError,
                    "StartTLS must precede a bind on this connection"), ct);
                return LdapDispatch.Continue;
            }

            if (stream.BufferedByteCount > 0)
            {
                _logger.LogWarning(
                    "ldap StartTLS refused: {Count} byte(s) were pipelined behind the request; closing the connection.",
                    stream.BufferedByteCount);
                await TrySendAsync(stream, LdapMessageWriter.NoticeOfDisconnection(
                    LdapResultCode.ProtocolError, "data was pipelined after a StartTLS request"), ct);
                return LdapDispatch.Close;
            }

            await SendAsync(stream, LdapMessageWriter.ExtendedResponse(
                messageId, LdapResultCode.Success, string.Empty, LdapProtocol.StartTlsOid), ct);

            System.Net.Security.SslStream ssl;
            try
            {
                ssl = await _tls.AuthenticateAsync(stream, _options.TlsHandshakeTimeout, ct);
            }
            catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException
                                          or IOException
                                          or OperationCanceledException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
            {
                // Sanitized: the handshake failure is logged here and never described on the wire.
                _logger.LogDebug(ex, "ldap StartTLS handshake failed; closing the connection.");
                return LdapDispatch.Close;
            }

            session.TlsEstablished = true;
            return LdapDispatch.Upgrade(new LdapBufferedStream(ssl));
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

        private LdapMessageReader NewReader() => new(
            _options.MaxMessageLength, _options.MaxNestingDepth, _options.MaxFilterComponents, _options.MaxSearchAttributes);

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

    /// <summary>
    /// What the connection loop does after one dispatched request: keep reading, close, or keep
    /// reading through a REPLACED stream (a completed StartTLS upgrade). Returning the upgraded
    /// stream rather than mutating shared state keeps the swap on the loop's own single-threaded
    /// path — the loop is the only thing that ever reads or writes the session's transport.
    /// </summary>
    internal readonly record struct LdapDispatch(bool KeepOpen, LdapBufferedStream? Upgraded)
    {
        public static LdapDispatch Continue => new(KeepOpen: true, Upgraded: null);

        public static LdapDispatch Close => new(KeepOpen: false, Upgraded: null);

        public static LdapDispatch Upgrade(LdapBufferedStream stream) => new(KeepOpen: true, Upgraded: stream);
    }
}

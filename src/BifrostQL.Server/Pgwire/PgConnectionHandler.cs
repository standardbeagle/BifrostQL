using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Kestrel connection handler for the PostgreSQL wire-protocol front door. Slice 1
    /// owns the connection up to an authenticated, ready session: SSLRequest → TLS
    /// upgrade, StartupMessage, cleartext or SCRAM-SHA-256 authentication, and the
    /// fail-closed projection of the login into a Bifrost identity through
    /// <see cref="IBifrostAuthContextFactory"/>. The simple/extended query protocol
    /// attaches at the clearly marked seam after ReadyForQuery.
    ///
    /// <para><b>Fail-closed identity is the load-bearing invariant.</b> A verified
    /// password only unlocks the <i>candidate</i> principal the credential store
    /// returned; the connection becomes ready only if that principal projects to a
    /// non-empty Bifrost user context. A subject-less principal, a principal from an
    /// OIDC issuer with no registered claim mapper, or any error during projection all
    /// send an ErrorResponse and close — the handshake never reaches AuthenticationOk
    /// with an anonymous or degraded identity.</para>
    /// </summary>
    internal sealed class PgConnectionHandler : ConnectionHandler
    {
        private readonly IPgCredentialStore _credentials;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly IServiceProvider _services;
        private readonly PgWireOptions _options;
        private readonly ILogger<PgConnectionHandler> _logger;

        public PgConnectionHandler(
            IPgCredentialStore credentials,
            IBifrostAuthContextFactory authFactory,
            IServiceProvider services,
            PgWireOptions options,
            ILogger<PgConnectionHandler>? logger = null)
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? NullLogger<PgConnectionHandler>.Instance;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            await using var stream = new DuplexPipeStream(connection.Transport);
            await HandleConnectionAsync(stream, connection.ConnectionClosed);
        }

        /// <summary>
        /// Drives one connection's startup + authentication over <paramref name="rawStream"/>.
        /// Written against a plain <see cref="Stream"/> so it runs identically over a
        /// real socket (tests, production) and the TLS-wrapped stream after upgrade.
        /// </summary>
        internal async Task HandleConnectionAsync(Stream rawStream, CancellationToken ct)
        {
            try
            {
                var (stream, startup) = await NegotiateStartupAsync(rawStream, ct);
                if (startup is null)
                    return; // client closed or a handled non-startup packet (Cancel/GSS)

                var parameters = PgProtocolIO.ParseStartupParameters(startup.Value.Span);
                if (!parameters.TryGetValue("user", out var username) || string.IsNullOrEmpty(username))
                {
                    await RejectAsync(stream, PgWireProtocol.SqlStateProtocolViolation,
                        "StartupMessage missing required 'user' parameter.", ct);
                    return;
                }

                var login = await _credentials.FindAsync(username, ct);
                bool verified;
                try
                {
                    verified = _options.AuthMethod == PgAuthMethod.Cleartext
                        ? await AuthenticateCleartextAsync(stream, login, ct)
                        : await AuthenticateScramAsync(stream, login, ct);
                }
                catch (PgScramProtocolException ex)
                {
                    // Malformed SCRAM from an unauthenticated peer is a protocol violation,
                    // not a wrong password: answer with a protocol_violation ErrorResponse
                    // and close cleanly on the (possibly TLS-wrapped) stream. The exception
                    // also derives from PgProtocolException, so even outside this guard it
                    // can never escape to Kestrel as unhandled.
                    await RejectAsync(stream, PgWireProtocol.SqlStateProtocolViolation, ex.Message, ct);
                    return;
                }
                if (!verified || login is null)
                {
                    await RejectAsync(stream, PgWireProtocol.SqlStateInvalidPassword,
                        "password authentication failed.", ct);
                    return;
                }

                // ---- Fail-closed identity gate ----
                // The password proved the caller holds the secret; it does NOT by itself
                // grant a Bifrost identity. Project the candidate principal through the
                // shared auth seam — the same one every HTTP/binary gate uses — and refuse
                // the connection unless it yields a real (non-empty) user context. Any
                // projection failure (subject-less principal, unmapped OIDC issuer) is a
                // rejection, never a fall-through to an anonymous session.
                if (!TryProjectIdentity(login, out var _userContext))
                {
                    await RejectAsync(stream, PgWireProtocol.SqlStateInvalidAuthorization,
                        "authenticated login does not map to an authorized identity.", ct);
                    return;
                }

                await CompleteHandshakeAsync(stream, ct);

                // ---- QUERY LOOP ----
                // The session is authenticated and ReadyForQuery has been sent. Slice 2
                // handles the simple query ('Q') path: reads run through IQueryIntentExecutor
                // under _userContext (the transformer pipeline is unskippable), results are
                // encoded as RowDescription/DataRow/CommandComplete, and query errors surface
                // as non-fatal ErrorResponses that leave the autocommit session usable.
                // Parse/Bind/Execute (extended protocol) and writes are later slices.
                await RunQueryLoopAsync(stream, _userContext, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or PgProtocolException or AuthenticationException)
            {
                // Expected connection-lifecycle faults: peer disconnect, cancellation,
                // wire-framing violations, TLS handshake failure. Nothing to send.
                _logger.LogDebug(ex, "pgwire connection ended: {Reason}", ex.Message);
            }
        }

        /// <summary>
        /// Handles the pre-startup negotiation packets (SSLRequest, GSSENCRequest,
        /// CancelRequest) and returns the TLS-appropriate stream plus the StartupMessage
        /// parameter body, or a null body when the connection was closed/handled without
        /// a startup.
        /// </summary>
        private async Task<(Stream Stream, ReadOnlyMemory<byte>? StartupBody)> NegotiateStartupAsync(
            Stream stream, CancellationToken ct)
        {
            while (true)
            {
                var (code, rest) = await PgProtocolIO.ReadStartupPacketAsync(stream, ct);
                switch (code)
                {
                    case PgWireProtocol.SslRequestCode:
                        if (_options.ServerCertificate is null)
                        {
                            // No certificate: decline TLS. The client decides whether to
                            // proceed in the clear or disconnect.
                            await PgProtocolIO.WriteRawByteAsync(stream, (byte)'N', ct);
                            continue;
                        }
                        await PgProtocolIO.WriteRawByteAsync(stream, (byte)'S', ct);
                        stream = await UpgradeToTlsAsync(stream, ct);
                        continue;

                    case PgWireProtocol.GssEncRequestCode:
                        await PgProtocolIO.WriteRawByteAsync(stream, (byte)'N', ct);
                        continue;

                    case PgWireProtocol.CancelRequestCode:
                        // No running query to cancel during the handshake; drop it.
                        return (stream, null);

                    case PgWireProtocol.ProtocolVersion3:
                        return (stream, rest);

                    default:
                        await RejectAsync(stream, PgWireProtocol.SqlStateProtocolViolation,
                            $"unsupported startup protocol code {code}.", ct);
                        return (stream, null);
                }
            }
        }

        private async Task<Stream> UpgradeToTlsAsync(Stream inner, CancellationToken ct)
        {
            var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _options.ServerCertificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
            return ssl;
        }

        /// <summary>AuthenticationCleartextPassword challenge; constant-time secret compare.</summary>
        private static async Task<bool> AuthenticateCleartextAsync(Stream stream, PgLogin? login, CancellationToken ct)
        {
            await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.AuthenticationRequest,
                PgBackend.AuthenticationCleartextPassword(), ct);

            var message = await PgProtocolIO.ReadMessageAsync(stream, ct);
            if (message.Type != PgWireProtocol.PasswordMessage)
                throw new PgProtocolException($"Expected PasswordMessage, got '{(char)message.Type}'.");

            var supplied = TrimTrailingNul(message.Body);
            // Compare against the real secret, or a random decoy for an unknown user, so
            // the reject path costs the same either way (no trivial user enumeration).
            // Run the fixed-time compare unconditionally BEFORE the null check so an
            // unknown user is not distinguishable from a wrong password by timing —
            // short-circuiting on `login is null` would skip the compare and leak it.
            var expected = Encoding.UTF8.GetBytes(login?.Secret ?? DecoySecret());
            var matches = CryptographicOperations.FixedTimeEquals(supplied, expected);
            return login is not null && matches;
        }

        /// <summary>AuthenticationSASL(SCRAM-SHA-256) exchange; the secret never crosses the wire.</summary>
        private async Task<bool> AuthenticateScramAsync(Stream stream, PgLogin? login, CancellationToken ct)
        {
            await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.AuthenticationRequest,
                PgBackend.AuthenticationSasl(PgWireProtocol.ScramSha256), ct);

            var initial = await PgProtocolIO.ReadMessageAsync(stream, ct);
            if (initial.Type != PgWireProtocol.PasswordMessage)
                throw new PgProtocolException($"Expected SASLInitialResponse, got '{(char)initial.Type}'.");

            var (mechanism, clientFirst) = ParseSaslInitialResponse(initial.Body);
            if (!string.Equals(mechanism, PgWireProtocol.ScramSha256, StringComparison.Ordinal))
                throw new PgProtocolException($"Unsupported SASL mechanism '{mechanism}'.");

            // Run the exchange even for an unknown user (decoy secret) so it fails at the
            // proof step like a wrong password, not with an earlier, distinguishable error.
            var scram = ScramSha256Server.Create(login?.Secret ?? DecoySecret());
            try
            {
                var serverFirst = scram.HandleClientFirst(clientFirst);
                await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.AuthenticationRequest,
                    PgBackend.AuthenticationSaslContinue(serverFirst), ct);

                var final = await PgProtocolIO.ReadMessageAsync(stream, ct);
                if (final.Type != PgWireProtocol.PasswordMessage)
                    throw new PgProtocolException($"Expected SASLResponse, got '{(char)final.Type}'.");

                var serverFinal = scram.HandleClientFinal(Encoding.UTF8.GetString(final.Body));
                await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.AuthenticationRequest,
                    PgBackend.AuthenticationSaslFinal(serverFinal), ct);
                return login is not null;
            }
            catch (PgScramAuthenticationException)
            {
                return false; // wrong secret — reject as invalid_password
            }
        }

        /// <summary>
        /// Projects the credential store's candidate principal through the shared auth
        /// seam. Returns false (fail closed) when projection throws or yields no identity.
        /// </summary>
        private bool TryProjectIdentity(PgLogin login, out IDictionary<string, object?> userContext)
        {
            userContext = new Dictionary<string, object?>();
            try
            {
                var carrier = new DefaultHttpContext { RequestServices = _services, User = login.Principal };
                var projected = _authFactory.CreateUserContext(carrier);
                if (projected.Count == 0)
                {
                    // An unauthenticated / claim-less principal projects to nothing. A
                    // successful pg login must map to a real identity — never anonymous.
                    _logger.LogWarning("pgwire login projected to an empty user context; rejecting.");
                    return false;
                }
                userContext = projected;
                return true;
            }
            catch (Exception ex)
            {
                // Subject-less principal, unmapped OIDC issuer, or any projection fault:
                // reject. Fail closed on every path — do not degrade to anonymous.
                _logger.LogWarning(ex, "pgwire identity projection failed; rejecting login.");
                return false;
            }
        }

        private static async Task CompleteHandshakeAsync(Stream stream, CancellationToken ct)
        {
            await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.AuthenticationRequest,
                PgBackend.AuthenticationOk(), ct);

            // Minimal ParameterStatus set so standard clients (psql, drivers) finish
            // connecting; the full runtime-parameter surface arrives with the query slice.
            await WriteParameterStatusAsync(stream, "server_version", "16.0 (BifrostQL)", ct);
            await WriteParameterStatusAsync(stream, "client_encoding", "UTF8", ct);
            await WriteParameterStatusAsync(stream, "DateStyle", "ISO, MDY", ct);

            await WriteBackendKeyDataAsync(stream, ct);
            await WriteReadyForQueryAsync(stream, ct);
        }

        /// <summary>
        /// Simple query protocol loop (autocommit only). Dispatches each frontend message:
        /// Terminate ends the session; a Query is executed and its result streamed back;
        /// any other message type is answered with a non-fatal feature_not_supported so the
        /// session stays usable. Wire-framing violations (<see cref="PgProtocolException"/>)
        /// are left to propagate to the outer handler, which tears the connection down.
        /// </summary>
        private async Task RunQueryLoopAsync(Stream stream, IDictionary<string, object?> userContext, CancellationToken ct)
        {
            while (true)
            {
                PgFrontendMessage message;
                try
                {
                    message = await PgProtocolIO.ReadMessageAsync(stream, ct);
                }
                catch (EndOfStreamException)
                {
                    return; // client disconnected
                }

                switch (message.Type)
                {
                    case PgWireProtocol.Terminate:
                        return;

                    case PgWireProtocol.Query:
                        await HandleSimpleQueryAsync(stream, message.Body, userContext, ct);
                        break;

                    default:
                        await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.ErrorResponse,
                            PgBackend.ErrorResponse(PgWireProtocol.SqlStateFeatureNotSupported,
                                $"unsupported message '{(char)message.Type}' (pgwire slice 2: simple query only).",
                                PgWireProtocol.SeverityError), ct);
                        await WriteReadyForQueryAsync(stream, ct);
                        break;
                }
            }
        }

        /// <summary>
        /// Executes one simple query and streams its result: RowDescription → DataRow* →
        /// CommandComplete. Reads route through <see cref="IQueryIntentExecutor"/> under
        /// <paramref name="userContext"/>, so the security transformer pipeline (tenant
        /// isolation, soft-delete, policy row/column scope) is applied unconditionally. A
        /// translation or execution error is caught and answered as a non-fatal
        /// ERROR-severity ErrorResponse; either way a ReadyForQuery closes the exchange and
        /// the autocommit session remains usable for the next query. Connection-lifecycle
        /// faults (IO, cancellation) and wire-framing violations are re-thrown to the outer
        /// handler untouched.
        /// </summary>
        private async Task HandleSimpleQueryAsync(Stream stream, byte[] body, IDictionary<string, object?> userContext, CancellationToken ct)
        {
            try
            {
                var sql = Encoding.UTF8.GetString(TrimTrailingNul(body));
                var executor = _services.GetService<IQueryIntentExecutor>()
                    ?? throw new BifrostExecutionError("pgwire endpoint has no registered query executor.");
                var translator = _services.GetService<IPgQueryTranslator>()
                    ?? throw new BifrostExecutionError("pgwire endpoint has no registered query translator.");

                var plan = await translator.TranslateAsync(executor, sql, userContext, _options.Endpoint, ct);
                var result = await executor.ExecuteAsync(plan.Intent, ct);

                await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.RowDescription,
                    PgBackend.RowDescription(plan.Columns), ct);

                foreach (var row in result.Rows)
                {
                    var values = plan.Columns
                        .Select(c => PgValueEncoder.ToText(row.TryGetValue(c.Name, out var v) ? v : null))
                        .ToList();
                    await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.DataRow,
                        PgBackend.DataRow(values), ct);
                }

                await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.CommandComplete,
                    PgBackend.CommandComplete($"SELECT {result.Rows.Count}"), ct);
            }
            catch (Exception ex) when (ex is not (IOException or OperationCanceledException or EndOfStreamException or PgProtocolException))
            {
                var (sqlState, clientMessage) = MapQueryError(ex);
                _logger.LogWarning(ex, "pgwire query failed ({SqlState}): {Message}", sqlState, ex.Message);
                await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.ErrorResponse,
                    PgBackend.ErrorResponse(sqlState, clientMessage, PgWireProtocol.SeverityError), ct);
            }

            await WriteReadyForQueryAsync(stream, ct);
        }

        /// <summary>
        /// Maps a query-phase exception to a client-safe (SQLSTATE, message) pair. A
        /// recognizer failure is a syntax error; a <see cref="BifrostExecutionError"/>
        /// already carries an authored, leak-free message (raw DB errors are sanitized at
        /// their source), so it passes through as internal_error. Anything else is reported
        /// generically — an unexpected exception's text is never forwarded to the wire.
        /// </summary>
        private static (string SqlState, string Message) MapQueryError(Exception ex) => ex switch
        {
            PgQueryTranslationException => (PgWireProtocol.SqlStateSyntaxError, ex.Message),
            BifrostExecutionError => (PgWireProtocol.SqlStateInternalError, ex.Message),
            _ => (PgWireProtocol.SqlStateInternalError, "internal error during query execution."),
        };

        private async Task RejectAsync(Stream stream, string sqlState, string message, CancellationToken ct)
        {
            _logger.LogWarning("pgwire handshake rejected ({SqlState}): {Message}", sqlState, message);
            await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.ErrorResponse,
                PgBackend.ErrorResponse(sqlState, message), ct);
        }

        private static async Task WriteParameterStatusAsync(Stream stream, string name, string value, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            ms.Write(nameBytes); ms.WriteByte(0);
            ms.Write(valueBytes); ms.WriteByte(0);
            await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.ParameterStatus, ms.ToArray(), ct);
        }

        private static async Task WriteBackendKeyDataAsync(Stream stream, CancellationToken ct)
        {
            var body = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), Environment.ProcessId);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), RandomNumberGenerator.GetInt32(int.MaxValue));
            await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.BackendKeyData, body, ct);
        }

        private static async Task WriteReadyForQueryAsync(Stream stream, CancellationToken ct)
            => await PgProtocolIO.WriteMessageAsync(stream, PgWireProtocol.ReadyForQuery,
                new[] { PgWireProtocol.TransactionStatusIdle }, ct);

        /// <summary>Parses a SASLInitialResponse body: mechanism C-string + Int32 length + client-first bytes.</summary>
        private static (string Mechanism, string ClientFirst) ParseSaslInitialResponse(byte[] body)
        {
            var nul = Array.IndexOf(body, (byte)0);
            if (nul < 0)
                throw new PgProtocolException("SASLInitialResponse missing mechanism terminator.");
            var mechanism = Encoding.UTF8.GetString(body, 0, nul);

            var offset = nul + 1;
            if (offset + 4 > body.Length)
                throw new PgProtocolException("SASLInitialResponse missing client-first length.");
            var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(offset, 4));
            offset += 4;
            // -1 means "no data"; a real SCRAM client-first is always present.
            if (length < 0 || offset + length > body.Length)
                throw new PgProtocolException("SASLInitialResponse client-first length out of range.");
            var clientFirst = Encoding.UTF8.GetString(body, offset, length);
            return (mechanism, clientFirst);
        }

        private static byte[] TrimTrailingNul(byte[] body)
            => body.Length > 0 && body[^1] == 0 ? body[..^1] : body;

        private static string DecoySecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }
}

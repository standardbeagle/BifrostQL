using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BifrostQL.Server.Pgwire; // DuplexPipeStream: shared Kestrel IDuplexPipe→Stream glue, not pgwire-specific.

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Kestrel connection handler for the Redis RESP-protocol front door. Slice 1 owns the
    /// connection loop and the plumbing command surface: the RESP2/RESP3 codec, command
    /// dispatch, and PING/HELLO/AUTH/SELECT/INFO — with <c>AUTH</c> establishing a Bifrost
    /// identity, fail-closed, through <see cref="IBifrostAuthContextFactory"/>. Data commands
    /// (GET/SET/HGETALL/SCAN…) attach at the <see cref="IRespCommandHandler"/> dispatch seam
    /// in later slices with no change to this loop.
    ///
    /// <para><b>Fail-closed identity is the load-bearing invariant.</b> A verified password
    /// only unlocks the <i>candidate</i> principal the credential store returned; the session
    /// becomes authenticated only if that principal projects to a non-empty Bifrost user
    /// context. A subject-less principal, a principal from an OIDC issuer with no registered
    /// claim mapper, or any projection error all fail the AUTH — the session never reaches an
    /// authenticated state with an anonymous or degraded identity. Unless a deployment
    /// explicitly clears <see cref="RespWireOptions.RequireAuthentication"/>, identity-bearing
    /// commands are refused with <c>NOAUTH</c> until AUTH succeeds.</para>
    /// </summary>
    internal sealed class RespConnectionHandler : ConnectionHandler
    {
        private readonly IRespCredentialStore _credentials;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly IServiceProvider _services;
        private readonly RespWireOptions _options;
        private readonly IReadOnlyDictionary<string, IRespCommandHandler> _dataHandlers;
        private readonly ILogger<RespConnectionHandler> _logger;
        private static long _connectionCounter;

        public RespConnectionHandler(
            IRespCredentialStore credentials,
            IBifrostAuthContextFactory authFactory,
            IServiceProvider services,
            RespWireOptions options,
            IEnumerable<IRespCommandHandler>? dataHandlers = null,
            ILogger<RespConnectionHandler>? logger = null)
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            // SLICE 2-5 attach data commands here: registered IRespCommandHandler instances are
            // indexed by their upper-case command name for O(1) case-insensitive dispatch below.
            _dataHandlers = (dataHandlers ?? Enumerable.Empty<IRespCommandHandler>())
                .ToDictionary(h => h.Name.ToUpperInvariant(), StringComparer.Ordinal);
            _logger = logger ?? NullLogger<RespConnectionHandler>.Instance;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            await using var stream = new DuplexPipeStream(connection.Transport);
            await HandleConnectionAsync(stream, connection.ConnectionClosed);
        }

        /// <summary>
        /// Drives one connection: read a command frame, dispatch it, write the reply, repeat.
        /// Written against a plain <see cref="Stream"/> so it runs identically over a real
        /// socket (tests, production). A wire-framing violation answers a clean protocol-error
        /// reply and closes (Redis semantics); connection-lifecycle faults are swallowed.
        /// </summary>
        internal async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
        {
            var session = new RespSession(Interlocked.Increment(ref _connectionCounter));
            var reader = new RespReader(stream, _options.MaxBulkLength, _options.MaxAggregateElements, _options.MaxNestingDepth);
            try
            {
                while (true)
                {
                    RespValue? frame;
                    try
                    {
                        frame = await reader.ReadValueAsync(ct);
                    }
                    catch (Exception ex) when (ex is RespProtocolException or FormatException or OverflowException or ArgumentException)
                    {
                        // Malformed wire input from a (possibly unauthenticated) peer. Only the
                        // adapter's own curated RespProtocolException text is client-safe; any BCL
                        // parse fault sanitizes to a generic string (never forward internal detail).
                        var detail = ex is RespProtocolException ? ex.Message : "invalid input.";
                        _logger.LogDebug(ex, "resp protocol error; closing connection: {Detail}", detail);
                        await RespWriter.WriteAsync(stream, RespValue.Err(RespProtocol.ProtocolErrorPrefix + detail), ct);
                        return; // Redis closes the connection after a protocol error.
                    }

                    if (frame is null)
                        return; // clean EOF: peer closed

                    var arguments = ParseCommand(frame);
                    if (arguments.Count == 0)
                        continue; // Redis silently ignores an empty multibulk.

                    var keepOpen = await DispatchAsync(stream, session, frame, arguments, ct);
                    if (!keepOpen)
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "resp connection ended: {Reason}", ex.Message);
            }
        }

        /// <summary>
        /// Interprets a decoded frame as a command: an array of bulk/simple strings whose
        /// first element is the command name. Anything else is a protocol violation.
        /// </summary>
        private static IReadOnlyList<string> ParseCommand(RespValue frame)
        {
            if (frame is not RespArray { Items: { } items })
                throw new RespProtocolException("expected an array of bulk strings for a command.");
            var arguments = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                arguments[i] = items[i] switch
                {
                    RespBulkString { Value: { } bytes } => Encoding.UTF8.GetString(bytes),
                    RespSimpleString s => s.Value,
                    _ => throw new RespProtocolException("command arguments must be bulk strings."),
                };
            }
            return arguments;
        }

        /// <summary>
        /// Dispatches one command case-insensitively. Returns whether the connection stays
        /// open (QUIT closes it). Plumbing commands that mutate session state are handled
        /// inline; every other name is looked up in the data-command seam, gated by auth.
        /// </summary>
        private async Task<bool> DispatchAsync(
            Stream stream, RespSession session, RespValue frame, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var name = arguments[0].ToUpperInvariant();
            switch (name)
            {
                case RespProtocol.Quit:
                    await Reply(stream, RespValue.Simple(RespProtocol.Ok), ct);
                    return false;

                case RespProtocol.Hello:
                    await HandleHelloAsync(stream, session, arguments, ct);
                    return true;

                case RespProtocol.Auth:
                    await HandleAuthAsync(stream, session, arguments, ct);
                    return true;

                case RespProtocol.Reset:
                    session.Reset();
                    await Reply(stream, RespValue.Simple(RespProtocol.ResetReply), ct);
                    return true;

                case RespProtocol.Ping:
                    if (await RequireAuthAsync(stream, session, ct))
                        await HandlePingAsync(stream, arguments, ct);
                    return true;

                case RespProtocol.Echo:
                    if (await RequireAuthAsync(stream, session, ct))
                        await HandleEchoAsync(stream, frame, ct);
                    return true;

                case RespProtocol.Select:
                    if (await RequireAuthAsync(stream, session, ct))
                        await HandleSelectAsync(stream, arguments, ct);
                    return true;

                case RespProtocol.Info:
                    if (await RequireAuthAsync(stream, session, ct))
                        await Reply(stream, RespValue.Bulk(BuildInfo(session)), ct);
                    return true;

                case RespProtocol.Command:
                    // redis-cli probes COMMAND DOCS on connect; a minimal empty reply lets it
                    // proceed without pretending to expose a command catalog.
                    if (await RequireAuthAsync(stream, session, ct))
                        await Reply(stream, RespValue.Arr(), ct);
                    return true;

                case RespProtocol.Client:
                    // redis-cli sends CLIENT SETINFO/SETNAME after HELLO; acknowledge minimally.
                    if (await RequireAuthAsync(stream, session, ct))
                        await Reply(stream, RespValue.Simple(RespProtocol.Ok), ct);
                    return true;

                default:
                    await DispatchDataCommandAsync(stream, session, name, arguments, ct);
                    return true;
            }
        }

        // ---- plumbing commands ------------------------------------------------

        private static async Task HandlePingAsync(Stream stream, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            // PING with no argument → +PONG; PING <msg> echoes the argument as a bulk string.
            var reply = arguments.Count > 1
                ? (RespValue)RespValue.Bulk(arguments[1])
                : RespValue.Simple(RespProtocol.Pong);
            await Reply(stream, reply, ct);
        }

        /// <summary>
        /// ECHO &lt;message&gt;. Returns the single message argument verbatim as a bulk string. Redis
        /// clients use ECHO as the per-connection handshake tracer, so answering it is what lets a real
        /// StackExchange.Redis / redis-cli client complete its connection to this front door.
        ///
        /// <para>Echoes the RAW argument bytes straight off the decoded frame, NOT the UTF-8-decoded
        /// command string: ECHO must be binary-safe (StackExchange.Redis's tracer is a 16-byte binary
        /// GUID), and round-tripping through the string dispatch path would re-encode non-UTF-8 bytes and
        /// change the bulk length, desyncing the client. This is the one command that needs the frame.</para>
        /// </summary>
        private static async Task HandleEchoAsync(Stream stream, RespValue frame, CancellationToken ct)
        {
            if (frame is RespArray { Items: { Count: 2 } items } && items[1] is RespBulkString { Value: { } raw })
            {
                await Reply(stream, new RespBulkString(raw), ct);
                return;
            }
            await Reply(stream, RespValue.Err(RespProtocol.WrongArgCount(RespProtocol.Echo)), ct);
        }

        /// <summary>
        /// SELECT db. BifrostQL exposes a single logical namespace, not Redis' 16 numbered
        /// databases; index 0 is accepted (Redis' default DB), any other index is rejected
        /// honestly with the standard out-of-range error rather than silently mapped. This is
        /// the documented SELECT semantics for this front door.
        /// </summary>
        private static async Task HandleSelectAsync(Stream stream, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (arguments.Count == 2 && int.TryParse(arguments[1], out var index) && index == 0)
            {
                await Reply(stream, RespValue.Simple(RespProtocol.Ok), ct);
                return;
            }
            await Reply(stream, RespValue.Err(RespProtocol.DbIndexOutOfRangeError), ct);
        }

        /// <summary>
        /// HELLO [protover [AUTH user pass] [SETNAME name]]. Negotiates the RESP protocol
        /// version, optionally authenticates inline, and returns the server-info reply as a
        /// map (RESP3) or a flat pair array (RESP2), per the negotiated version. On an
        /// auth-required front door, HELLO without an established identity and without inline
        /// AUTH is refused with NOAUTH — the protocol is not switched.
        /// </summary>
        private async Task HandleHelloAsync(
            Stream stream, RespSession session, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var requestedProtocol = session.Protocol;
            var index = 1;

            if (index < arguments.Count && !IsHelloOption(arguments[index]))
            {
                if (!int.TryParse(arguments[index], out var protocol) ||
                    (protocol != RespProtocol.Resp2 && protocol != RespProtocol.Resp3))
                {
                    await Reply(stream, RespValue.Err(RespProtocol.NoProtoError), ct);
                    return;
                }
                requestedProtocol = protocol;
                index++;
            }

            string? authUser = null, authPass = null, setName = null;
            while (index < arguments.Count)
            {
                var option = arguments[index].ToUpperInvariant();
                if (option == RespProtocol.HelloAuthOption && index + 2 < arguments.Count)
                {
                    authUser = arguments[index + 1];
                    authPass = arguments[index + 2];
                    index += 3;
                }
                else if (option == RespProtocol.HelloSetNameOption && index + 1 < arguments.Count)
                {
                    setName = arguments[index + 1];
                    index += 2;
                }
                else
                {
                    await Reply(stream, RespValue.Err("ERR Syntax error in HELLO"), ct);
                    return;
                }
            }

            if (authUser is not null)
            {
                if (!await TryAuthenticateAsync(session, authUser, authPass ?? string.Empty, ct))
                {
                    await Reply(stream, RespValue.Err(RespProtocol.WrongPassError), ct);
                    return;
                }
            }
            else if (_options.RequireAuthentication && !session.IsAuthenticated)
            {
                await Reply(stream, RespValue.Err(RespProtocol.HelloNoAuthError), ct);
                return;
            }

            if (setName is not null)
                session.Name = setName;
            session.Protocol = requestedProtocol;
            await Reply(stream, BuildHelloReply(session), ct);
        }

        /// <summary>AUTH &lt;password&gt; (default user) or AUTH &lt;user&gt; &lt;password&gt;.</summary>
        private async Task HandleAuthAsync(
            Stream stream, RespSession session, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            string user, pass;
            switch (arguments.Count)
            {
                case 2:
                    user = RespProtocol.DefaultUser;
                    pass = arguments[1];
                    break;
                case 3:
                    user = arguments[1];
                    pass = arguments[2];
                    break;
                default:
                    await Reply(stream, RespValue.Err("ERR wrong number of arguments for 'auth' command"), ct);
                    return;
            }

            if (!await TryAuthenticateAsync(session, user, pass, ct))
            {
                // Redis keeps the connection usable after a failed AUTH so the client can retry.
                await Reply(stream, RespValue.Err(RespProtocol.WrongPassError), ct);
                return;
            }
            await Reply(stream, RespValue.Simple(RespProtocol.Ok), ct);
        }

        // ---- authentication (fail-closed) ------------------------------------

        /// <summary>
        /// Verifies the supplied password against the resolved login in constant time and,
        /// only on a match, projects the candidate principal into a Bifrost identity. Returns
        /// false — leaving the session unauthenticated — on an unknown user, a wrong password,
        /// or an identity that does not project (never establishes an anonymous session).
        /// </summary>
        private async Task<bool> TryAuthenticateAsync(RespSession session, string user, string pass, CancellationToken ct)
        {
            var login = await _credentials.FindAsync(user, ct);

            // Run the fixed-time compare unconditionally against the real secret or a random
            // decoy, BEFORE the null/existence check, so an unknown user is not distinguishable
            // from a wrong password by timing (short-circuiting on `login is null` would leak it).
            var expected = Encoding.UTF8.GetBytes(login?.Secret ?? DecoySecret());
            var supplied = Encoding.UTF8.GetBytes(pass);
            var matches = CryptographicOperations.FixedTimeEquals(supplied, expected);
            if (login is null || !matches)
                return false;

            // The password proved the caller holds the secret; it does NOT by itself grant a
            // Bifrost identity. Project the candidate principal through the shared auth seam and
            // refuse unless it yields a real (non-empty) user context — fail closed, never anonymous.
            if (!TryProjectIdentity(login, out var userContext))
                return false;

            session.Authenticate(userContext);
            return true;
        }

        /// <summary>
        /// Projects the credential store's candidate principal through the shared auth seam —
        /// the same one every HTTP/binary gate uses. Returns false (fail closed) when
        /// projection throws (subject-less principal, unmapped OIDC issuer) or yields no identity.
        /// </summary>
        private bool TryProjectIdentity(RespLogin login, out IDictionary<string, object?> userContext)
        {
            userContext = new Dictionary<string, object?>();
            try
            {
                var carrier = new DefaultHttpContext { RequestServices = _services, User = login.Principal };
                var projected = _authFactory.CreateUserContext(carrier);
                if (projected.Count == 0)
                {
                    _logger.LogWarning("resp login projected to an empty user context; rejecting.");
                    return false;
                }
                userContext = projected;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "resp identity projection failed; rejecting login.");
                return false;
            }
        }

        // ---- data-command seam + auth gate -----------------------------------

        private async Task DispatchDataCommandAsync(
            Stream stream, RespSession session, string name, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            // SLICE 2-5 attach data commands here: a registered IRespCommandHandler answers
            // read/write commands under the authenticated identity. Slice 1 registers none, so
            // every non-plumbing command is an unknown command.
            if (!_dataHandlers.TryGetValue(name, out var handler))
            {
                await Reply(stream, RespValue.Err(RespProtocol.UnknownCommand(arguments)), ct);
                return;
            }
            if (handler.RequiresAuthentication && !await RequireAuthAsync(stream, session, ct))
                return;

            var reply = await handler.HandleAsync(
                new RespCommandContext(arguments, session, _services, _options.Endpoint), ct);
            await Reply(stream, reply, ct);
        }

        /// <summary>
        /// Enforces the identity gate for a command that needs one: on an auth-required front
        /// door, an unauthenticated session is answered NOAUTH and the command is skipped.
        /// Returns whether the command may proceed.
        /// </summary>
        private async Task<bool> RequireAuthAsync(Stream stream, RespSession session, CancellationToken ct)
        {
            if (!_options.RequireAuthentication || session.IsAuthenticated)
                return true;
            await Reply(stream, RespValue.Err(RespProtocol.NoAuthError), ct);
            return false;
        }

        // ---- replies ----------------------------------------------------------

        private static Task Reply(Stream stream, RespValue value, CancellationToken ct)
            => RespWriter.WriteAsync(stream, value, ct);

        /// <summary>Builds the HELLO server-info reply: a RESP3 map or a RESP2 flat pair array, per the negotiated version.</summary>
        private static RespValue BuildHelloReply(RespSession session)
        {
            var entries = new (string Key, RespValue Value)[]
            {
                (RespProtocol.HelloServer, RespValue.Bulk(RespProtocol.ServerName)),
                (RespProtocol.HelloVersion, RespValue.Bulk(RespProtocol.ServerVersion)),
                (RespProtocol.HelloProto, RespValue.Int(session.Protocol)),
                (RespProtocol.HelloId, RespValue.Int(session.Id)),
                (RespProtocol.HelloMode, RespValue.Bulk(RespProtocol.ServerMode)),
                (RespProtocol.HelloRole, RespValue.Bulk(RespProtocol.ServerRole)),
                (RespProtocol.HelloModules, new RespArray(Array.Empty<RespValue>())),
            };

            if (session.Protocol == RespProtocol.Resp3)
            {
                return new RespMap(entries
                    .Select(e => new KeyValuePair<RespValue, RespValue>(RespValue.Bulk(e.Key), e.Value))
                    .ToList());
            }

            var flat = new List<RespValue>(entries.Length * 2);
            foreach (var (key, value) in entries)
            {
                flat.Add(RespValue.Bulk(key));
                flat.Add(value);
            }
            return new RespArray(flat);
        }

        /// <summary>Builds a minimal, parseable INFO <c># Server</c> section (<c>key:value</c> lines).</summary>
        private static string BuildInfo(RespSession session)
        {
            var sb = new StringBuilder();
            sb.Append("# Server\r\n");
            sb.Append($"redis_version:{RespProtocol.ServerVersion}\r\n");
            sb.Append($"server_name:{RespProtocol.ServerName}\r\n");
            sb.Append($"redis_mode:{RespProtocol.ServerMode}\r\n");
            sb.Append($"role:{RespProtocol.ServerRole}\r\n");
            sb.Append($"connected_client_id:{session.Id}\r\n");
            sb.Append($"proto:{session.Protocol}\r\n");
            return sb.ToString();
        }

        private static string DecoySecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        private static bool IsHelloOption(string argument)
        {
            var upper = argument.ToUpperInvariant();
            return upper == RespProtocol.HelloAuthOption || upper == RespProtocol.HelloSetNameOption;
        }
    }
}

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Auth;
using GraphQL;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// ASP.NET Core middleware that serves BifrostQL queries over WebSocket binary frames.
    /// Accepts WebSocket upgrade requests at the configured path, reads protobuf-encoded
    /// BifrostMessage envelopes from binary frames, executes them via IBifrostEngine,
    /// and sends protobuf-encoded responses back as binary frames.
    ///
    /// Requests on one connection run concurrently, up to
    /// <see cref="MaxConcurrentRequestsPerConnection"/>. A single reader owns the receive side
    /// and routes each ChunkAck/ChunkNack to the transfer of the request_id it names; every
    /// send goes through one serialized writer, so the frames of different requests interleave
    /// and the client matches them by request_id. A slow or large response no longer blocks the
    /// requests behind it. A request_id must be unique among the requests in flight; a reused
    /// one is refused with an Error frame coded <see cref="BinaryErrorCode.RequestIdInFlight"/>.
    ///
    /// Large responses exceeding the chunk threshold are automatically split into
    /// Chunk messages with CRC32 integrity checksums and backpressure via ChunkAck.
    /// Incoming chunked requests from clients are reassembled before execution.
    ///
    /// Supports retry within a connection: clients can send Resume or ChunkNack messages
    /// to request retransmission of chunks from the ChunkBuffer. The buffer is scoped to
    /// a single connection — request_id values are client-chosen, so buffered chunks are
    /// never visible to any other connection (which would leak one principal's results to
    /// another). Completed transfers are removed from the buffer immediately; interrupted
    /// transfers are released with the connection.
    /// </summary>
    public sealed class BifrostBinaryMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IBifrostEngine _engine;
        private readonly string _endpointPath;
        private readonly ILogger<BifrostBinaryMiddleware> _logger;
        private readonly int _chunkThreshold;
        private readonly int _ackWindow;

        /// <summary>
        /// How long a chunked response waits for the next ack once its window is full.
        /// <see cref="ChunkSender.DefaultAckTimeout"/> (60 s) unless the mount states otherwise.
        /// Expiry abandons only the stalled response, with a <c>CHUNK_ACK_TIMEOUT</c> Error frame.
        /// <see cref="ChunkSender.NoAckTimeout"/> is the explicit opt-out: the window is then pure
        /// backpressure and only <see cref="_idleTimeout"/> cuts off a silent peer.
        /// </summary>
        private readonly TimeSpan _ackTimeout;

        /// <summary>The clock the ack timeout runs on.</summary>
        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// Origins permitted to open a cross-origin WebSocket handshake. Empty (or null)
        /// means same-origin only. A WebSocket handshake bypasses CORS, so without this
        /// guard any web page could open a socket on the victim's cookie (CSWSH) and run
        /// authenticated queries. Matched case-insensitively against the request Origin
        /// header; requests with no Origin header (non-browser clients) are allowed since
        /// they are not a cross-site-request-forgery vector.
        /// </summary>
        private readonly IReadOnlyList<string> _allowedOrigins;

        /// <summary>
        /// Maximum binary frame size (4 MB). Messages larger than this are rejected.
        /// </summary>
        private const int MaxFrameSize = 4 * 1024 * 1024;

        /// <summary>
        /// The per-connection receive buffer, rented from the shared pool. A message larger than
        /// this arrives over several receives and is accumulated in a pooled stream up to
        /// <see cref="MaxFrameSize"/>, so an idle connection holds 64 KiB, not a 4 MiB
        /// large-object-heap array.
        /// </summary>
        internal const int ReceiveBufferSize = 64 * 1024;

        /// <summary>Default concurrent-connection cap for one binary mount.</summary>
        public const int DefaultMaxConnections = 100;

        /// <summary>
        /// Default deadline for the first frame of an admitted connection. The admission slot
        /// is taken at the upgrade, before any frame is read, so a peer that connects and then
        /// says nothing would otherwise hold a slot (and its buffers) for free.
        /// </summary>
        public static readonly TimeSpan DefaultFirstFrameTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Default idle deadline between frames of an established connection. Generous, because
        /// an authenticated binary session is a pooled client that legitimately sits idle.
        /// </summary>
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Default deadline for one socket write (a frame, or a close frame). A peer that stops
        /// reading (a zero TCP window) would otherwise park the write, the send lock behind it
        /// and the reader waiting on that lock forever, so the idle deadline never runs again and
        /// the admission slot is never released. A write past this deadline aborts the connection.
        /// </summary>
        public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether this mount serves an endpoint that requires authentication. When true, an
        /// upgrade whose caller does not project to a real Bifrost identity is closed before a
        /// single frame is read — the binary transport carries the same GraphQL surface as the
        /// HTTP endpoint it serves, so it must carry that endpoint's auth requirement too.
        /// <see cref="BifrostServiceCollectionExtensions.UseBifrostBinary"/> — the supported
        /// mount — resolves this from the served endpoint's own configuration and fails closed
        /// when it cannot; there is no default here for a caller to inherit by accident.
        /// </summary>
        private readonly bool _requireAuthenticatedIdentity;

        /// <summary>
        /// Admission counter for THIS mount. A distinct instance per mount, so two binary
        /// endpoints never share one budget.
        /// </summary>
        private readonly BinaryTransportConnectionLimiter _limiter;

        private readonly TimeSpan _firstFrameTimeout;
        private readonly TimeSpan _idleTimeout;
        private readonly TimeSpan _writeTimeout;

        /// <summary>Admitted connections on this mount (diagnostics/tests).</summary>
        internal int ActiveConnections => _limiter.Count;

        /// <summary>
        /// Builds each connection's upload reassembler (tests set smaller caps through it).
        /// </summary>
        internal Func<ChunkReceiver> CreateChunkReceiver { get; init; } = static () => new ChunkReceiver();

        public BifrostBinaryMiddleware(
            RequestDelegate next,
            IBifrostEngine engine,
            string endpointPath,
            ILogger<BifrostBinaryMiddleware> logger)
            : this(next, engine, endpointPath, logger,
                   ChunkSender.DefaultChunkThreshold,
                   ChunkSender.DefaultAckWindow)
        {
        }

        public BifrostBinaryMiddleware(
            RequestDelegate next,
            IBifrostEngine engine,
            string endpointPath,
            ILogger<BifrostBinaryMiddleware> logger,
            int chunkThreshold,
            int ackWindow)
            : this(next, engine, endpointPath, logger, chunkThreshold, ackWindow, ChunkSender.DefaultAckTimeout,
                   // Fail closed: a host constructing the middleware without stating a posture
                   // gets the safe one, never anonymous access.
                   requireAuthenticatedIdentity: true)
        {
        }

        public BifrostBinaryMiddleware(
            RequestDelegate next,
            IBifrostEngine engine,
            string endpointPath,
            ILogger<BifrostBinaryMiddleware> logger,
            int chunkThreshold,
            int ackWindow,
            TimeSpan ackTimeout,
            bool requireAuthenticatedIdentity,
            IReadOnlyList<string>? allowedOrigins = null,
            int maxConnections = DefaultMaxConnections,
            TimeSpan firstFrameTimeout = default,
            TimeSpan idleTimeout = default,
            TimeSpan writeTimeout = default,
            TimeProvider? timeProvider = null)
        {
            _next = next;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _chunkThreshold = chunkThreshold;
            _ackWindow = ackWindow;
            if (ackTimeout <= TimeSpan.Zero && ackTimeout != ChunkSender.NoAckTimeout)
                throw new ArgumentOutOfRangeException(nameof(ackTimeout));
            _ackTimeout = ackTimeout;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _requireAuthenticatedIdentity = requireAuthenticatedIdentity;
            _allowedOrigins = allowedOrigins ?? Array.Empty<string>();
            _limiter = new BinaryTransportConnectionLimiter(maxConnections);
            // default (zero) means "this mount did not state one": take the class default.
            // A negative deadline is a configuration error, not a disable switch.
            if (firstFrameTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(firstFrameTimeout));
            if (idleTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            if (writeTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(writeTimeout));
            _firstFrameTimeout = firstFrameTimeout == default ? DefaultFirstFrameTimeout : firstFrameTimeout;
            _idleTimeout = idleTimeout == default ? DefaultIdleTimeout : idleTimeout;
            _writeTimeout = writeTimeout == default ? DefaultWriteTimeout : writeTimeout;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await _next(context);
                return;
            }

            // Reject cross-origin handshakes BEFORE upgrading. WebSocket handshakes bypass
            // CORS, and the binary transport authenticates via the ambient cookie, so an
            // unchecked upgrade is a Cross-Site WebSocket Hijacking (CSWSH) vector: any page
            // could open a socket on the victim's session. No upgrade is performed on reject.
            if (!IsOriginAllowed(context))
            {
                _logger.LogWarning(
                    "Rejected cross-origin WebSocket handshake from Origin '{Origin}'",
                    context.Request.Headers.Origin.ToString());
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Admission slot at ACCEPT, before the upgrade and before the identity gate below.
            // A cap applied after authentication would bound admitted SESSIONS but not the work
            // an unauthenticated peer can force (each connection costs a 4 MB receive buffer and
            // a reassembly budget), which is not a cap on the resource.
            if (!_limiter.TryAcquire())
            {
                _logger.LogWarning(
                    "Refused binary WebSocket upgrade: the mount's {Max}-connection limit is reached",
                    _limiter.Max);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            try
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                // Fail-closed identity gate, per mount and order-independent. The GraphQL
                // endpoints put theirs inside their own Map branch, which never covered this
                // mount: an anonymous peer reached the engine with the EMPTY user context an
                // unauthenticated CreateUserContext returns, and the query ran under it. The
                // refusal speaks only of the transport's auth requirement — it is identical for
                // an unknown caller, a subject-less principal and an unmappable token, so it is
                // no account-existence oracle.
                if (_requireAuthenticatedIdentity
                    && (await BifrostIdentityGate.ProjectAsync(context)).Outcome != BifrostIdentityOutcome.Projected)
                {
                    _logger.LogWarning(
                        "Closed binary WebSocket: the endpoint this mount serves requires authentication");
                    await TryCloseAsync(
                        webSocket, WebSocketCloseStatus.PolicyViolation, "Authentication required");
                    return;
                }

                await HandleConnectionAsync(webSocket, context);
            }
            finally
            {
                _limiter.Release();
            }
        }

        /// <summary>
        /// Whether the request's Origin header is permitted to open the WebSocket. A request
        /// with no Origin header is allowed (non-browser client; not a CSRF vector). When an
        /// allowlist is configured the Origin must appear in it; otherwise only same-origin
        /// (scheme + host + port equal to the request's own host) is permitted.
        /// </summary>
        private bool IsOriginAllowed(HttpContext context)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (string.IsNullOrEmpty(origin))
                return true; // Non-browser client: browsers always send Origin on WS handshakes.

            if (_allowedOrigins.Count > 0)
            {
                foreach (var allowed in _allowedOrigins)
                {
                    if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            return IsSameOrigin(origin, context.Request);
        }

        private static bool IsSameOrigin(string origin, HttpRequest request)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                return false;

            var requestHost = request.Host;
            if (!requestHost.HasValue)
                return false;

            var expectedPort = requestHost.Port
                ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            var originPort = originUri.IsDefaultPort
                ? (string.Equals(originUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : originUri.Port;

            return string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(originUri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)
                && originPort == expectedPort;
        }

        /// <summary>
        /// The per-connection primitives that are created once in
        /// <see cref="HandleConnectionAsync"/> and always travel together through message
        /// processing: the socket, its owning HTTP context, the shared receive buffer, and
        /// the connection-scoped chunk receiver/sender/buffer. Bundled so the processing
        /// methods take one context instead of a six-argument parameter list.
        /// </summary>
        /// <summary>
        /// Most requests one connection executes at once. A request past the cap is refused
        /// with an Error frame coded <see cref="BinaryErrorCode.TooManyConcurrentRequests"/>, never queued: the reader must stay free to route the
        /// acks that let running transfers finish. The HTTP endpoint's counterpart is Kestrel's
        /// per-connection HTTP/2 stream limit.
        /// </summary>
        public const int MaxConcurrentRequestsPerConnection = 16;

        /// <summary>
        /// How long the reader waits for the peer to echo a close frame the server sent after a
        /// request-side failure, before it drops the connection.
        /// </summary>
        public static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// One binary connection's runtime. A single reader (<see cref="HandleConnectionAsync"/>)
        /// owns the socket's receive side; every request runs as its own task and every frame
        /// goes out through <see cref="SendAsync"/>, which serializes writes. A chunked response
        /// is a <see cref="ChunkTransfer"/> registered under its request id, so the reader hands
        /// it the acks the client sends for it.
        /// </summary>
        private sealed class BinaryConnection : IDisposable
        {
            /// <summary>
            /// Below this payload size, copying the payload into one frame array costs less than a
            /// second WebSocket send (an extra frame header and flush). An estimate, not a measured
            /// crossover; it sits well under the 64 KiB default chunk so every full chunk splits.
            /// </summary>
            internal const int FragmentedSendMinPayload = 16 * 1024;

            private readonly SemaphoreSlim _sendLock = new(1, 1);
            private readonly TimeSpan _writeTimeout;
            private readonly TimeProvider _timeProvider;
            private readonly ILogger _logger;
            private readonly ConcurrentDictionary<Task, byte> _running = new();

            // Request ids executing or transferring on this connection.
            private readonly ConcurrentDictionary<uint, InFlightRequest> _inFlight = new();

            private sealed class InFlightRequest
            {
                /// <summary>Set once the response is being chunked.</summary>
                public volatile ChunkTransfer? Transfer;

                /// <summary>The request's task; it releases the id as its last step.</summary>
                public Task? Work;
            }

            public BinaryConnection(
                WebSocket webSocket,
                HttpContext httpContext,
                ChunkReceiver chunkReceiver,
                ChunkSender chunkSender,
                ChunkBuffer chunkBuffer,
                TimeSpan writeTimeout,
                TimeProvider timeProvider,
                ILogger logger)
            {
                ChunkReceiver = chunkReceiver;
                _writeTimeout = writeTimeout;
                _timeProvider = timeProvider;
                _logger = logger;
                WebSocket = webSocket;
                HttpContext = httpContext;
                ChunkSender = chunkSender;
                ChunkBuffer = chunkBuffer;
                Closing = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                InputEnded = CancellationTokenSource.CreateLinkedTokenSource(Closing.Token);
                ReaderStop = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                // HttpContext is not thread-safe, and every request task reads its User (the
                // session check, the identity projection). The first read installs the
                // authentication feature when no auth middleware ran, which writes the feature
                // collection: two tasks doing that at once corrupt it. Read it here, before the
                // reader starts any task, so the tasks only ever find it installed.
                _ = httpContext.User;
            }

            public WebSocket WebSocket { get; }
            public HttpContext HttpContext { get; }
            public ChunkReceiver ChunkReceiver { get; }
            public ChunkSender ChunkSender { get; }
            public ChunkBuffer ChunkBuffer { get; }

            /// <summary>Cancelled when the connection ends: stops every request and transfer on it.</summary>
            public CancellationTokenSource Closing { get; }

            public CancellationToken Token => Closing.Token;

            /// <summary>
            /// Cancelled when the reader stops, including on the client's Close frame. A
            /// transfer waits on acks the reader routes, so it stops here; a request still
            /// executing keeps <see cref="Token"/> and sends its answer, which a peer that
            /// sent Close may still receive.
            /// </summary>
            public CancellationTokenSource InputEnded { get; }

            /// <summary>
            /// Ends the reader's wait for the peer's close echo after the server sent the close
            /// frame (a request-side failure or the read deadline). Never cancelled otherwise:
            /// cancelling a pending receive aborts a real WebSocket without the close frame.
            /// </summary>
            public CancellationTokenSource ReaderStop { get; }

            // Arms ReaderStop CloseHandshakeTimeout after the close frame, on the injected clock.
            private ITimer? _readerStopTimer;
            private int _readerStopArmed;

            private int _readDeadlinePassed;
            private Task _readDeadlineClose = Task.CompletedTask;

            /// <summary>
            /// The close the read deadline started (<see cref="ReadDeadlinePassed"/>), or a
            /// completed task. Awaited before the connection is disposed.
            /// </summary>
            public Task ReadDeadlineClose => Volatile.Read(ref _readDeadlineClose);

            /// <summary>True once <see cref="ReadDeadlinePassed"/> has started the close.</summary>
            public bool ReadDeadlineHasPassed => Volatile.Read(ref _readDeadlinePassed) != 0;

            /// <summary>
            /// The peer sent nothing within the read deadline. The pending receive is left
            /// running, because cancelling it would abort a real WebSocket before the close frame
            /// goes out: the PolicyViolation close is sent instead, the requests stop, and the
            /// reader ends on the peer's echo or <see cref="CloseHandshakeTimeout"/> later.
            /// Called from the deadline's timer, at most once per connection.
            /// </summary>
            public void ReadDeadlinePassed()
            {
                if (Interlocked.Exchange(ref _readDeadlinePassed, 1) != 0)
                    return;
                _logger.LogDebug("Binary connection exceeded its read deadline; closing");
                Volatile.Write(ref _readDeadlineClose,
                    FailFromRequestAsync(WebSocketCloseStatus.PolicyViolation, "Read deadline exceeded"));
            }

            /// <summary>The status the connection ends with: a fault's, else a normal closure.</summary>
            public (WebSocketCloseStatus Status, string Reason) CloseWith { get; private set; }
                = (WebSocketCloseStatus.NormalClosure, "Connection closed");

            public int InFlightCount => _inFlight.Count;

            /// <summary>
            /// Claims <paramref name="requestId"/> and starts <paramref name="work"/> for it on the
            /// thread pool. False when the id is already in flight. An id whose transfer the client
            /// has fully acked counts as finished, which a client may assume as soon as it has every
            /// chunk: the new request takes the id over and its task waits for the old one (only
            /// queued acks left) before it runs. The wait happens on the new request's task, never
            /// on the caller: the caller is the connection's single reader, and blocking it would
            /// stop ack routing for every other transfer on the socket.
            /// </summary>
            public bool TryStart(uint requestId, Func<Task> work)
            {
                var entry = new InFlightRequest();
                Task? priorWork = null;
                // The prior request's task removes its own entry when it ends, which can happen
                // between any two of these steps; an entry that vanished is retried, not refused.
                while (!_inFlight.TryAdd(requestId, entry))
                {
                    if (!_inFlight.TryGetValue(requestId, out var prior))
                        continue;
                    if (prior.Transfer is not { AckedByClient: true } || prior.Work is not { } work0)
                        return false;
                    if (_inFlight.TryUpdate(requestId, entry, prior))
                    {
                        // The prior task's own removal is keyed on its entry, so it leaves ours alone.
                        priorWork = work0;
                        break;
                    }
                }

                var task = Task.Run(async () =>
                {
                    try
                    {
                        if (priorWork != null)
                            await priorWork;
                        await work();
                    }
                    finally
                    {
                        _inFlight.TryRemove(new KeyValuePair<uint, InFlightRequest>(requestId, entry));
                    }
                });
                entry.Work = task;
                _running[task] = 0;
                _ = task.ContinueWith(static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
                    _running, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return true;
            }

            public void SetTransfer(uint requestId, ChunkTransfer transfer)
            {
                if (_inFlight.TryGetValue(requestId, out var entry))
                    entry.Transfer = transfer;
            }

            public ChunkTransfer? TransferFor(uint requestId)
                => _inFlight.TryGetValue(requestId, out var entry) ? entry.Transfer : null;

            /// <summary>Waits for every request task. Each catches its own faults.</summary>
            public Task DrainAsync() => Task.WhenAll(_running.Keys);

            /// <summary>
            /// Sends one frame as one WebSocket message. Serialization happens outside the lock;
            /// the lock only orders the writes, which a WebSocket allows one at a time. A payload
            /// of at least <see cref="FragmentedSendMinPayload"/> bytes goes out as a second
            /// fragment after the envelope, so it is never copied into a frame-sized array.
            /// The write runs under the write deadline (<see cref="WriteWithDeadlineAsync"/>); the
            /// wait for the lock is bounded by the same deadline on whichever write holds it.
            /// </summary>
            public async Task SendAsync(BifrostMessage message, CancellationToken cancellationToken)
            {
                var payload = message.Payload;
                var fragmented = payload.Length >= FragmentedSendMinPayload;
                var head = fragmented ? message.EncodeEnvelope() : message.ToBytes();
                await _sendLock.WaitAsync(cancellationToken);
                try
                {
                    await WriteWithDeadlineAsync(async token =>
                    {
                        await WebSocket.SendAsync(head, WebSocketMessageType.Binary, endOfMessage: !fragmented, token);
                        if (fragmented)
                            await WebSocket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, token);
                    }, cancellationToken);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            /// <summary>
            /// Runs one socket write under the write deadline, on the injected clock. A peer that
            /// stops reading parks the write once the transport's buffers fill; past the deadline
            /// the connection is aborted (<see cref="AbortStalledPeer"/>), which ends the reader
            /// and every request, so the admission slot is released. The caller sees the
            /// cancellation, as it would for a connection that ended under it.
            /// </summary>
            private async Task WriteWithDeadlineAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
            {
                using var deadline = new CancellationTokenSource(_writeTimeout, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                try
                {
                    await write(linked.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    AbortStalledPeer();
                    throw;
                }
            }

            /// <summary>
            /// A write missed its deadline: the peer is not reading. No close frame can reach it
            /// (that is a write too), so the socket is aborted and the reader and requests stop.
            /// </summary>
            private void AbortStalledPeer()
            {
                _logger.LogWarning(
                    "Binary peer did not take a frame within the {WriteTimeout} write deadline; aborting the connection",
                    _writeTimeout);
                CloseWith = (WebSocketCloseStatus.PolicyViolation, "Write deadline exceeded");
                WebSocket.Abort();
                try
                {
                    Closing.Cancel();
                    ReaderStop.Cancel();
                }
                catch (ObjectDisposedException) { }
            }

            /// <summary>
            /// The close handshake at the end of the connection, once nothing else writes. Bounded
            /// by <see cref="CloseHandshakeTimeout"/> on the injected clock: a peer that neither
            /// takes the close frame nor echoes it is aborted instead of holding the slot.
            /// </summary>
            public async Task CloseAsync()
            {
                if (WebSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                    return;
                using var deadline = new CancellationTokenSource(CloseHandshakeTimeout, _timeProvider);
                try
                {
                    await WebSocket.CloseAsync(CloseWith.Status, CloseWith.Reason, deadline.Token);
                }
                catch (WebSocketException)
                {
                    // Already closed, ignore
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Binary peer did not complete the close handshake; aborting the connection");
                    WebSocket.Abort();
                }
            }

            /// <summary>
            /// A fault the reader found (deadline, malformed or non-binary frame): stops every
            /// request. The reader breaks out of its loop and closes with this status once the
            /// requests have unwound.
            /// </summary>
            public void FailFromReader(WebSocketCloseStatus status, string reason)
            {
                CloseWith = (status, reason);
                Closing.Cancel();
            }

            /// <summary>
            /// A fault a request task found (retransmit cap, a failed send): sends
            /// the close frame after any frame being written, and stops every request. The
            /// reader keeps its receive, sees the peer echo the close and ends; a peer that
            /// never echoes is cut off after <see cref="CloseHandshakeTimeout"/>.
            /// </summary>
            public async Task FailFromRequestAsync(WebSocketCloseStatus status, string reason)
            {
                CloseWith = (status, reason);
                try
                {
                    await _sendLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (WebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            await WriteWithDeadlineAsync(
                                token => WebSocket.CloseOutputAsync(status, reason, token), CancellationToken.None);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
                catch (WebSocketException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                Closing.Cancel();
                StopReaderAfterCloseHandshake();
            }

            /// <summary>
            /// Cancels <see cref="ReaderStop"/> once <see cref="CloseHandshakeTimeout"/> passes on
            /// the injected clock. The first close frame starts the wait; a later one does not
            /// extend it.
            /// </summary>
            private void StopReaderAfterCloseHandshake()
            {
                if (Interlocked.Exchange(ref _readerStopArmed, 1) != 0)
                    return;
                Volatile.Write(ref _readerStopTimer, _timeProvider.CreateTimer(static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException) { }
                }, ReaderStop, CloseHandshakeTimeout, Timeout.InfiniteTimeSpan));
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _readerStopTimer, null)?.Dispose();
                ReaderStop.Dispose();
                InputEnded.Dispose();
                Closing.Dispose();
                _sendLock.Dispose();
            }
        }

        private async Task HandleConnectionAsync(WebSocket webSocket, HttpContext httpContext)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
            try
            {
                await HandleConnectionAsync(webSocket, httpContext, buffer);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task HandleConnectionAsync(WebSocket webSocket, HttpContext httpContext, byte[] buffer)
        {

            // Chunk buffering is connection-scoped. request_id values are chosen by the
            // client, so a shared buffer would let one connection replay another
            // connection's buffered results via Resume/ChunkNack (cross-principal leak)
            // or corrupt an entry by reusing the same request_id concurrently.
            var chunkBuffer = new ChunkBuffer();
            var chunkSender = new ChunkSender(
                _chunkThreshold, _ackWindow, chunkBuffer, _ackTimeout, timeProvider: _timeProvider);
            using var connection = new BinaryConnection(
                webSocket, httpContext, CreateChunkReceiver(), chunkSender, chunkBuffer, _writeTimeout, _timeProvider, _logger);

            try
            {
                var isFirstFrame = true;
                while (webSocket.State == WebSocketState.Open)
                {
                    ArraySegment<byte> messageBytes;
                    WebSocketMessageType messageType;
                    PooledBufferStream? fragments;

                    // Every read carries a deadline. The first one is short: the admission slot
                    // is already held at that point, so a peer that connects and never speaks
                    // would otherwise pin a slot and a receive buffer for nothing. Later reads get
                    // the idle deadline — an established session is a pooled client that may
                    // legitimately sit quiet between queries. The deadline runs on the injected
                    // clock and never cancels the receive (that would abort a real socket before
                    // the close frame): it sends the close, and the receive ends on the echo.
                    using var readDeadline = new CancellationTokenSource(
                        isFirstFrame ? _firstFrameTimeout : _idleTimeout, _timeProvider);
                    using var onReadDeadline = readDeadline.Token.Register(connection.ReadDeadlinePassed);
                    isFirstFrame = false;

                    try
                    {
                        (messageBytes, messageType, fragments) = await ReadFullMessageAsync(webSocket, buffer, connection.ReaderStop.Token);
                    }
                    catch (OperationCanceledException) when (connection.ReaderStop.IsCancellationRequested
                        && !httpContext.RequestAborted.IsCancellationRequested)
                    {
                        _logger.LogDebug("Binary peer did not echo the close frame; dropping the connection");
                        webSocket.Abort();
                        break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // A malformed reassembly or an oversized (>MaxFrameSize) multi-frame
                        // message. A single garbage frame must not kill the connection with an
                        // unhandled exception and no close handshake — send an error frame and
                        // close the connection cleanly instead.
                        await FailFromReaderWithErrorAsync(connection, ex, "Invalid or oversized frame");
                        break;
                    }

                    using var ownedFragments = fragments;

                    // The deadline covers the wait for a frame, not its handling.
                    onReadDeadline.Dispose();
                    // A frame that raced the deadline is not served: the close is already out.
                    if (messageType == WebSocketMessageType.Close || connection.ReadDeadlineHasPassed)
                        break;

                    if (messageType != WebSocketMessageType.Binary)
                    {
                        connection.FailFromReader(WebSocketCloseStatus.InvalidMessageType, "Only binary frames are supported");
                        break;
                    }

                    try
                    {
                        await ReceiveFrameAsync(messageBytes, connection);
                    }
                    catch (Exception ex) when (
                        ex is not OperationCanceledException
                        && !(ex is WebSocketException wse && wse.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely))
                    {
                        // A malformed reassembled message, a garbage ack/control frame, or any
                        // other processing fault for THIS message must not escape the loop and
                        // tear the connection down without a close handshake. Report it as an
                        // error frame and close cleanly.
                        await FailFromReaderWithErrorAsync(connection, ex, "Message processing failed");
                        break;
                    }
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                _logger.LogDebug("WebSocket connection closed prematurely");
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("WebSocket connection cancelled");
            }
            finally
            {
                // The reader is done, so nothing can route acks any more: stop the transfers,
                // let requests still executing send their answers, then close. A fault or an
                // abort has already cancelled Closing, which stops the requests too.
                connection.InputEnded.Cancel();
                await connection.DrainAsync();
                // The read deadline's close runs off the deadline timer; it ends before disposal.
                await connection.ReadDeadlineClose;
                // Every request has ended, so nothing else writes: the full close handshake
                // is safe here.
                await connection.CloseAsync();
            }
        }

        private const string MalformedFrameMessage = "Malformed or unprocessable frame; closing connection.";

        /// <summary>
        /// The reader could not process a frame (malformed, oversized, a garbage control
        /// frame): sends a generic Error frame (no internal detail) and ends the connection
        /// with a protocol close, so the client sees a clean close rather than an unhandled
        /// exception.
        /// </summary>
        private async Task FailFromReaderWithErrorAsync(BinaryConnection connection, Exception ex, string reason)
        {
            _logger.LogWarning(ex, "Closing binary connection: {Reason}", reason);
            await TrySendErrorFrameAsync(connection, MalformedFrameMessage, null);
            connection.FailFromReader(WebSocketCloseStatus.ProtocolError, "Malformed frame");
        }

        /// <summary>
        /// Sends an Error frame, swallowing the WebSocket/cancellation faults that arise when
        /// the socket is already tearing down. Used on the abort paths where a best-effort
        /// notification is wanted but the connection is about to close regardless.
        /// </summary>
        private static async Task TrySendErrorFrameAsync(BinaryConnection connection, string message, uint? requestId)
        {
            try
            {
                var errorResponse = new BifrostMessage { Type = BifrostMessageType.Error };
                if (requestId.HasValue)
                    errorResponse.RequestId = requestId.Value;
                errorResponse.Errors.Add(message);
                await connection.SendAsync(errorResponse, CancellationToken.None);
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Closes the socket with the given status/reason, swallowing the WebSocket/cancellation
        /// faults that arise when the socket was already aborted. Only for a socket no reader is
        /// receiving on (the pre-loop identity refusal); a running connection uses
        /// <see cref="BinaryConnection.FailFromRequestAsync"/>.
        /// </summary>
        private static async Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
        {
            try
            {
                await webSocket.CloseAsync(status, reason, CancellationToken.None);
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Handles one frame on the reader. Control frames (acks, NACKs, upload chunks) are
        /// handled here, in order; a Query, Mutation or Resume starts its own task so the
        /// reader is free for the next frame at once.
        /// </summary>
        private async Task ReceiveFrameAsync(ArraySegment<byte> messageBytes, BinaryConnection connection)
        {
            // Parsed straight from the receive buffer: the parser copies every field it keeps,
            // and the reader does not receive again until this returns.
            BifrostMessage frame;
            try
            {
                frame = BifrostMessage.FromBytes(messageBytes.Array!, messageBytes.Offset, messageBytes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize binary message");
                var errorResponse = new BifrostMessage
                {
                    Type = BifrostMessageType.Error,
                    Errors = { "Server error occurred" },
                };
                await connection.SendAsync(errorResponse, connection.Token);
                return;
            }

            // Client chunks carry fragments of a full serialized BifrostMessage; the
            // assembled bytes are deserialized to recover the original Query/Mutation. The
            // assembled bytes stay charged to the connection's reassembly cap until the request
            // they carried ends: whoever ends up holding the request releases them.
            var heldReassemblyBytes = 0;
            if (frame.Type == BifrostMessageType.Chunk)
            {
                var reassembled = await HandleChunkAsync(frame, connection);
                if (reassembled is not { } upload)
                    return; // More chunks expected, or reassembly failed.
                (frame, heldReassemblyBytes) = upload;
            }

            try
            {
                switch (frame.Type)
                {
                    case BifrostMessageType.ChunkAck:
                        // An ack for no running transfer (late, or for a finished one) is noise.
                        if (connection.TransferFor(frame.RequestId) is { } acked)
                            acked.Acknowledge(frame);
                        return;
                    case BifrostMessageType.ChunkNack:
                        if (connection.TransferFor(frame.RequestId) is { } transfer)
                            transfer.Acknowledge(frame);
                        else
                            await HandleNackAsync(frame, connection);
                        return;
                    default:
                        var held = heldReassemblyBytes;
                        heldReassemblyBytes = 0; // StartRequestAsync owns the release from here.
                        await StartRequestAsync(frame, connection, held);
                        return;
                }
            }
            finally
            {
                if (heldReassemblyBytes > 0)
                    connection.ChunkReceiver.ReleaseAssembled(heldReassemblyBytes);
            }
        }

        /// <summary>
        /// Starts a Query, Mutation or Resume on its own task, or refuses it with an Error
        /// frame when its request id is already in flight (acks could not be routed) or the
        /// connection is at <see cref="MaxConcurrentRequestsPerConnection"/>.
        /// <paramref name="heldReassemblyBytes"/> is the reassembly charge of a request that
        /// arrived chunked; it is released when the request's task ends, or at once when the
        /// request is refused.
        /// </summary>
        private async Task StartRequestAsync(BifrostMessage request, BinaryConnection connection, int heldReassemblyBytes)
        {
            var releaseHere = heldReassemblyBytes;
            try
            {
                await StartOrRefuseAsync();
            }
            finally
            {
                if (releaseHere > 0)
                    connection.ChunkReceiver.ReleaseAssembled(releaseHere);
            }

            async Task StartOrRefuseAsync()
            {
                if (connection.InFlightCount >= MaxConcurrentRequestsPerConnection)
                {
                    await SendErrorResponseAsync(connection, request.RequestId,
                        $"Too many concurrent requests: a connection runs at most {MaxConcurrentRequestsPerConnection} at once. " +
                        "Resend this request when one completes.",
                        BinaryErrorCode.TooManyConcurrentRequests);
                    return;
                }

                var started = connection.TryStart(request.RequestId, () => RunRequestAsync(request, connection, heldReassemblyBytes));
                if (started)
                {
                    releaseHere = 0; // The request's task releases it.
                    return;
                }

                await SendErrorResponseAsync(connection, request.RequestId,
                    $"Request id {request.RequestId} is already in flight on this connection.",
                    BinaryErrorCode.RequestIdInFlight);
            }
        }

        /// <summary>
        /// One request's task: runs it, maps its faults, and releases its reassembly charge
        /// (if it arrived chunked) as its last step.
        /// </summary>
        private async Task RunRequestAsync(BifrostMessage request, BinaryConnection connection, int heldReassemblyBytes)
        {
            try
            {
                try
                {
                    if (!await SessionStillLiveAsync(connection))
                        return;
                    if (request.Type == BifrostMessageType.Resume)
                        await HandleResumeAsync(request, connection);
                    else
                        await HandleQueryAsync(request, connection);
                }
                catch (OperationCanceledException) when (connection.InputEnded.IsCancellationRequested)
                {
                    // The connection ended under the request.
                }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "Binary request {RequestId} lost its socket", request.RequestId);
                    await connection.FailFromRequestAsync(WebSocketCloseStatus.EndpointUnavailable, "Send failed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Binary request {RequestId} failed; closing connection", request.RequestId);
                    await TrySendErrorFrameAsync(connection, MalformedFrameMessage, request.RequestId);
                    await connection.FailFromRequestAsync(WebSocketCloseStatus.ProtocolError, "Malformed frame");
                }
            }
            finally
            {
                if (heldReassemblyBytes > 0)
                    connection.ChunkReceiver.ReleaseAssembled(heldReassemblyBytes);
            }
        }

        /// <summary>
        /// Re-runs the local-auth session check for each message. The upgrade request's cookie was
        /// validated once, at the handshake, and the connection outlives it: a session ended since
        /// (logout, administrator revoke, password or privilege change, idle or absolute expiry)
        /// closes the connection with PolicyViolation instead of serving the message. A refreshed
        /// principal replaces the connection's, so later messages project the current roles and
        /// tenant. An enrollment-only session (a user who still owes a second factor) is refused the
        /// same way: the socket is not an enrollment endpoint. Principals from other providers are not
        /// governed by local sessions.
        /// </summary>
        private static async Task<bool> SessionStillLiveAsync(BinaryConnection connection)
        {
            var http = connection.HttpContext;
            var check = await LocalAuthEndpoint.RevalidateSessionAsync(http.User, http, connection.Token);
            switch (check.Verdict)
            {
                case LocalSessionVerdict.Rejected:
                    await connection.FailFromRequestAsync(WebSocketCloseStatus.PolicyViolation, "Session ended");
                    return false;
                case LocalSessionVerdict.EnrollmentOnly:
                    await connection.FailFromRequestAsync(WebSocketCloseStatus.PolicyViolation, "Second factor required");
                    return false;
                case LocalSessionVerdict.Refreshed:
                    http.User = check.Principal!;
                    return true;
                case LocalSessionVerdict.NotLocal:
                case LocalSessionVerdict.Live:
                    return true;
                default:
                    throw new InvalidOperationException($"Unhandled local session verdict {check.Verdict}.");
            }
        }

        /// <summary>
        /// Adds an incoming client chunk to the reassembly buffer and acknowledges it once it is
        /// accepted. Returns the reconstructed original request once the final chunk arrives, or
        /// null when more chunks are still expected or reassembly failed (an Error frame and no
        /// ack are sent in that case, so a client driving its send window off acks never
        /// advances past a chunk the server rejected). The reassembled request comes with its
        /// payload's length, which stays charged to the connection's reassembly cap until the
        /// caller releases it.
        /// </summary>
        private async Task<(BifrostMessage Request, int HeldBytes)?> HandleChunkAsync(BifrostMessage request, BinaryConnection connection)
        {
            byte[]? assembledBytes;
            try
            {
                assembledBytes = connection.ChunkReceiver.AddChunk(request);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Chunk reassembly failed for request {RequestId}", request.RequestId);
                await SendErrorResponseAsync(connection, request.RequestId, "Invalid chunk");
                return null;
            }

            try
            {
                await connection.SendAsync(ChunkReceiver.CreateAck(request.RequestId, request.ChunkSequence), connection.Token);

                if (assembledBytes == null)
                    return null; // More chunks expected

                return (BifrostMessage.FromBytes(assembledBytes), assembledBytes.Length);
            }
            catch
            {
                // Nobody will hold this request: hand its charge back before the fault unwinds.
                if (assembledBytes != null)
                    connection.ChunkReceiver.ReleaseAssembled(assembledBytes.Length);
                throw;
            }
        }

        /// <summary>
        /// ChunkNack for no running transfer: retransmit the requested chunk from this
        /// connection's buffer (never another connection's transfers).
        /// </summary>
        private async Task HandleNackAsync(BifrostMessage request, BinaryConnection connection)
        {
            var retransmitChunk = connection.ChunkBuffer.TryGet(request.RequestId, request.ChunkSequence);
            if (retransmitChunk != null)
            {
                _logger.LogDebug(
                    "Retransmitting chunk {Sequence} for request {RequestId} (NACK)",
                    request.ChunkSequence, request.RequestId);
                await connection.SendAsync(retransmitChunk, connection.Token);
            }
            else
            {
                await SendErrorResponseAsync(
                    connection, request.RequestId,
                    $"Chunk {request.ChunkSequence} not available for retransmission");
            }
        }

        /// <summary>
        /// Resume: client wants chunks of an interrupted transfer on this connection
        /// retransmitted from last_sequence + 1. The buffer is connection-scoped, so
        /// transfers from other (or previous) connections are never resumable here.
        /// </summary>
        private async Task HandleResumeAsync(BifrostMessage request, BinaryConnection connection)
        {
            var remainingChunks = connection.ChunkBuffer.GetChunksAfter(request.RequestId, request.LastSequence);
            if (remainingChunks.Count > 0)
            {
                _logger.LogDebug(
                    "Resuming request {RequestId} from sequence {LastSequence}: {Count} chunks to retransmit",
                    request.RequestId, request.LastSequence, remainingChunks.Count);

                // Send ResumeAck to confirm resumption is starting
                var resumeAck = new BifrostMessage
                {
                    RequestId = request.RequestId,
                    Type = BifrostMessageType.ResumeAck,
                    ChunkTotal = (uint)remainingChunks.Count,
                    LastSequence = request.LastSequence,
                };
                await connection.SendAsync(resumeAck, connection.Token);

                await RunTransferAsync(connection, request.RequestId, remainingChunks);
                return;
            }

            // Transfer expired or unknown: send ResumeAck with 0 chunks
            _logger.LogDebug(
                "Resume request {RequestId}: no chunks available (expired or unknown)",
                request.RequestId);
            var emptyResumeAck = new BifrostMessage
            {
                RequestId = request.RequestId,
                Type = BifrostMessageType.ResumeAck,
                ChunkTotal = 0,
                LastSequence = request.LastSequence,
            };
            await connection.SendAsync(emptyResumeAck, connection.Token);
        }

        /// <summary>
        /// Executes a Query/Mutation request and sends the response, chunking it when it
        /// exceeds the chunk threshold.
        /// </summary>
        private async Task HandleQueryAsync(BifrostMessage request, BinaryConnection connection)
        {
            var response = await ExecuteRequestAsync(request, connection.HttpContext, connection.Token);

            if (connection.ChunkSender.RequiresChunking(response))
            {
                _logger.LogDebug(
                    "Chunking response for request {RequestId}: {PayloadSize} bytes",
                    response.RequestId, response.Payload.Length);
                await RunTransferAsync(connection, response.RequestId, connection.ChunkSender.SplitIntoChunks(response));
                return;
            }

            await connection.SendAsync(response, connection.Token);
        }

        /// <summary>
        /// Runs a chunked send registered under its request id, so the reader routes its acks.
        /// Releases the buffered chunks on full delivery instead of letting the payload linger
        /// until TTL expiry. A stated ack timeout that expires abandons only this response, with
        /// a coded Error frame; a per-sequence retransmit-limit breach ends the connection.
        /// </summary>
        private async Task RunTransferAsync(BinaryConnection connection, uint requestId, IReadOnlyList<BifrostMessage> chunks)
        {
            var transfer = connection.ChunkSender.StartTransfer(chunks, connection.SendAsync);
            connection.SetTransfer(requestId, transfer);
            try
            {
                await transfer.RunAsync(connection.InputEnded.Token);
                connection.ChunkBuffer.Complete(requestId);
            }
            catch (TimeoutException ex)
            {
                // Only reachable when the mount states an ack timeout. The client is still
                // connected (a silent one meets the idle deadline instead), and its other requests
                // are healthy, so abandon this response alone and say why with a code it can act on.
                // Late acks for it find no transfer and are dropped.
                _logger.LogWarning(
                    ex, "Chunk acknowledgement timeout for request {RequestId}; abandoning the response", requestId);
                connection.ChunkBuffer.Complete(requestId);
                await SendErrorResponseAsync(connection, requestId,
                    $"No chunk acknowledgement within {_ackTimeout.TotalSeconds:0.###} seconds; the response was abandoned.",
                    BinaryErrorCode.ChunkAckTimeout);
            }
            catch (ChunkRetransmitLimitExceededException ex)
            {
                // A client that perpetually NACKs the same chunk. It is told why before the
                // connection closes, so it surfaces a real failure instead of hanging.
                _logger.LogWarning(
                    ex, "Chunk retransmission limit exceeded for request {RequestId}; aborting transfer", requestId);
                await TrySendErrorFrameAsync(connection, "Chunk retransmission limit exceeded; transfer aborted.", requestId);
                await connection.FailFromRequestAsync(WebSocketCloseStatus.PolicyViolation, "Chunk retransmission limit exceeded");
            }
        }

        private static Task SendErrorResponseAsync(
            BinaryConnection connection, uint requestId, string message, string errorCode = "")
        {
            var errorResponse = new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.Error,
                ErrorCode = errorCode,
            };
            errorResponse.Errors.Add(message);
            return connection.SendAsync(errorResponse, connection.Token);
        }

        private async Task<BifrostMessage> ExecuteRequestAsync(
            BifrostMessage request, HttpContext httpContext, CancellationToken cancellationToken)
        {
            var response = new BifrostMessage
            {
                RequestId = request.RequestId,
                Type = BifrostMessageType.Result,
            };

            if (request.Type != BifrostMessageType.Query && request.Type != BifrostMessageType.Mutation)
            {
                response.Type = BifrostMessageType.Error;
                response.Errors.Add($"Unsupported message type: {request.Type}");
                return response;
            }

            if (string.IsNullOrEmpty(request.Query))
            {
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Query text is required");
                return response;
            }

            // Each message gets its own DI scope. A connection runs several messages at once
            // and lives for many, so the upgrade request's scope would share scoped services
            // (a host's grant resolver and the DbContext it reads) across concurrent requests
            // and memoize them for the whole session. Outside the catch-all below: a host with
            // no request services is a wiring fault, not a failed query.
            await using var scope = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>()
                .CreateAsyncScope();

            IReadOnlyDictionary<string, object?>? variables;
            try
            {
                variables = ParseVariables(request.VariablesJson, scope.ServiceProvider);
            }
            catch (JsonException)
            {
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Invalid variables JSON");
                return response;
            }

            try
            {
                // Per-message projection through the SAME shared gate as the upgrade-time
                // check: one identity decision per mount, and a projection fault (unmapped
                // OIDC issuer, subject-less principal) becomes an error frame here instead
                // of escaping the message loop (M13). It projects against this message's scope,
                // so a scoped grant resolver is never shared by two concurrent messages.
                var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(httpContext, scope.ServiceProvider);
                if (outcome == BifrostIdentityOutcome.Unprojectable)
                {
                    response.Type = BifrostMessageType.Error;
                    response.Errors.Add("The caller identity is not accepted by this deployment.");
                    return response;
                }

                var bifrostRequest = new BifrostRequest
                {
                    Query = request.Query,
                    Variables = variables,
                    UserContext = userContext,
                    RequestServices = scope.ServiceProvider,
                    CancellationToken = cancellationToken,
                };

                var result = await _engine.ExecuteAsync(bifrostRequest, _endpointPath);

                if (!result.IsSuccess)
                {
                    // Deliberate GraphQL-wire parity, not an invariant-3 gap: these are the
                    // SAME GraphQL ExecutionError messages the HTTP path returns for the
                    // same query under the same identity (GraphQLFrontend.MapResult; raw
                    // driver text is already redacted upstream by
                    // BifrostExecutionError.FromDatabaseException). The binary transport is
                    // an alternate wire for the GraphQL surface — the editor toggles
                    // between them — so diverging here would leak nothing less and only
                    // desynchronize the two transports' error contracts. If this transport
                    // ever serves a non-GraphQL surface, that surface must map conditions
                    // through its own funnel instead of this pass-through.
                    //
                    // The envelope's errors are strings, with no slot for the extensions.code
                    // the HTTP wire carries, so an operation-id refusal leads with its code in
                    // the text, worded as on the other non-GraphQL wires: the client must act on
                    // it (send an id, use a new one) and must be able to tell it from a fault.
                    foreach (var error in result.Errors)
                        response.Errors.Add(OperationIdWireRefusal.TextOfCode(CodeOf(error)) ?? error.Message);
                }

                if (result.Data != null)
                {
                    response.Payload = await SerializeResultPayloadAsync(result, scope.ServiceProvider, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing binary request {RequestId}", request.RequestId);
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Query execution failed");
            }

            return response;
        }

        private static string? CodeOf(BifrostResultError error)
            => error.Extensions is not null
                && error.Extensions.TryGetValue(BifrostEngine.CodeExtension, out var code)
                    ? code as string
                    : null;

        /// <summary>
        /// Serializes the execution result's data into the binary Payload using the same
        /// registered <see cref="IGraphQLSerializer"/> the HTTP path (GraphQLFrontend) uses.
        /// <see cref="BifrostResult.Data"/> is a GraphQL.NET ExecutionNode graph that only the
        /// registered serializer renders into the correct GraphQL wire shape
        /// (<c>{"data":{...}}</c>); a bare System.Text.Json pass would emit the node internals.
        /// Falls back to System.Text.Json only when no serializer is resolvable (e.g. a unit
        /// test with no DI container), where the data is already a plain object graph.
        /// </summary>
        private static async Task<byte[]> SerializeResultPayloadAsync(
            BifrostResult result, IServiceProvider services, CancellationToken cancellationToken)
        {
            var serializer = services.GetService<IGraphQLSerializer>();
            if (serializer == null)
                // Same {"data":...} envelope as the serializer path below: the client
                // unwraps exactly one envelope level unconditionally, so the wire shape
                // must be uniform — a bare-data payload here would make the client's
                // unwrap eat the caller's first result field.
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["data"] = result.Data });

            // Executed must be true or the GraphQL serializer omits the "data" field entirely.
            var executionResult = new ExecutionResult { Data = result.Data, Executed = true };
            if (result.Errors.Count > 0)
            {
                var errors = new ExecutionErrors();
                foreach (var error in result.Errors)
                    errors.Add(new ExecutionError(error.Message));
                executionResult.Errors = errors;
            }

            using var buffer = new PooledBufferStream();
            await serializer.WriteAsync(buffer, executionResult, cancellationToken);
            return buffer.ToArray();
        }

        private static IReadOnlyDictionary<string, object?>? ParseVariables(string variablesJson, IServiceProvider services)
        {
            if (string.IsNullOrEmpty(variablesJson))
                return null;

            // Deserialize with the registered GraphQL serializer — the SAME path the
            // HTTP frontend takes — so variable values arrive as the native graph the
            // executor can coerce (Inputs). A bare System.Text.Json pass leaves every
            // value as a JsonElement, which GraphQL.NET rejects with "Unable to
            // convert '(object)' to '<Type>'" — every variable-carrying binary query
            // (any paged grid read: $sort/$limit/$offset) failed on it while
            // variable-less queries sailed through, which is why the gap survived.
            //
            // Malformed variables JSON must surface as an error rather than silently
            // executing the operation with no variables (which would run against
            // wrong/default inputs). The caller maps JsonException to an Error reply.
            var serializer = services.GetService<IGraphQLTextSerializer>();
            if (serializer != null)
                return serializer.Deserialize<Inputs>(variablesJson);
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(variablesJson);
        }

        /// <summary>
        /// Reads a complete WebSocket message. A message that fits one receive is returned as a
        /// view of <paramref name="buffer"/> with no copy. A longer one is accumulated in a pooled
        /// stream, returned as the third element for the caller to dispose once the message is
        /// handled; the size cap is checked before each fragment is retained, so an oversized
        /// message is refused holding at most <see cref="MaxFrameSize"/> bytes.
        /// </summary>
        internal static async Task<(ArraySegment<byte> Data, WebSocketMessageType Type, PooledBufferStream? Owner)> ReadFullMessageAsync(
            WebSocket webSocket, byte[] buffer, CancellationToken cancellationToken)
        {
            var segment = new ArraySegment<byte>(buffer, 0, Math.Min(buffer.Length, ReceiveBufferSize));
            var result = await webSocket.ReceiveAsync(segment, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return (ArraySegment<byte>.Empty, WebSocketMessageType.Close, null);

            if (result.EndOfMessage)
                return (new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, null);

            var fragments = new PooledBufferStream();
            try
            {
                fragments.Write(buffer, 0, result.Count);
                while (!result.EndOfMessage)
                {
                    result = await webSocket.ReceiveAsync(segment, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        fragments.Dispose();
                        return (ArraySegment<byte>.Empty, WebSocketMessageType.Close, null);
                    }

                    if (fragments.Length + result.Count > MaxFrameSize)
                        throw new InvalidOperationException("Message exceeds maximum size of " + MaxFrameSize + " bytes");
                    fragments.Write(buffer, 0, result.Count);
                }

                return (fragments.Written, result.MessageType, fragments);
            }
            catch
            {
                fragments.Dispose();
                throw;
            }
        }
    }
}

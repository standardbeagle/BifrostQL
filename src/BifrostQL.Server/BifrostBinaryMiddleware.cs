using System.Net.WebSockets;
using System.Text.Json;
using BifrostQL.Core.Resolvers;
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
    /// Processing is strictly serial per connection: <see cref="HandleConnectionAsync"/>
    /// fully processes one message (including sending any chunked response) before reading
    /// the next frame. The request_id on each message identifies which request a response
    /// or control frame (ChunkAck/ChunkNack/Resume) belongs to; it does NOT enable
    /// concurrent in-flight requests. A Query/Mutation frame that arrives while a chunked
    /// response is still being sent cannot be serviced and is rejected with an Error rather
    /// than silently dropped.
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
        private readonly TimeSpan _ackTimeout;

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
            : this(next, engine, endpointPath, logger, chunkThreshold, ackWindow, ChunkSender.DefaultAckTimeout)
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
            IReadOnlyList<string>? allowedOrigins = null)
        {
            _next = next;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _chunkThreshold = chunkThreshold;
            _ackWindow = ackWindow;
            _ackTimeout = ackTimeout;
            _allowedOrigins = allowedOrigins ?? Array.Empty<string>();
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

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await HandleConnectionAsync(webSocket, context);
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
        private sealed class BinaryConnectionContext
        {
            public BinaryConnectionContext(
                WebSocket webSocket,
                HttpContext httpContext,
                byte[] receiveBuffer,
                ChunkReceiver chunkReceiver,
                ChunkSender chunkSender,
                ChunkBuffer chunkBuffer)
            {
                WebSocket = webSocket;
                HttpContext = httpContext;
                ReceiveBuffer = receiveBuffer;
                ChunkReceiver = chunkReceiver;
                ChunkSender = chunkSender;
                ChunkBuffer = chunkBuffer;
            }

            public WebSocket WebSocket { get; }
            public HttpContext HttpContext { get; }
            public byte[] ReceiveBuffer { get; }
            public ChunkReceiver ChunkReceiver { get; }
            public ChunkSender ChunkSender { get; }
            public ChunkBuffer ChunkBuffer { get; }
            public CancellationToken RequestAborted => HttpContext.RequestAborted;
        }

        private async Task HandleConnectionAsync(WebSocket webSocket, HttpContext httpContext)
        {
            var buffer = new byte[MaxFrameSize];
            var chunkReceiver = new ChunkReceiver();

            // Chunk buffering is connection-scoped. request_id values are chosen by the
            // client, so a shared buffer would let one connection replay another
            // connection's buffered results via Resume/ChunkNack (cross-principal leak)
            // or corrupt an entry by reusing the same request_id concurrently.
            var chunkBuffer = new ChunkBuffer();
            var chunkSender = new ChunkSender(_chunkThreshold, _ackWindow, chunkBuffer, _ackTimeout, _logger);
            var connection = new BinaryConnectionContext(
                webSocket, httpContext, buffer, chunkReceiver, chunkSender, chunkBuffer);

            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    byte[] messageBytes;
                    WebSocketMessageType messageType;
                    try
                    {
                        (messageBytes, messageType) = await ReadFullMessageAsync(webSocket, buffer, httpContext.RequestAborted);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // A malformed reassembly or an oversized (>MaxFrameSize) multi-frame
                        // message. A single garbage frame must not kill the connection with an
                        // unhandled exception and no close handshake — send an error frame and
                        // close the connection cleanly instead.
                        await CloseWithErrorAsync(webSocket, ex, "Invalid or oversized frame");
                        break;
                    }

                    if (messageType == WebSocketMessageType.Close)
                        break;

                    if (messageType != WebSocketMessageType.Binary)
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "Only binary frames are supported",
                            CancellationToken.None);
                        break;
                    }

                    try
                    {
                        await ProcessMessageAsync(messageBytes, connection);
                    }
                    catch (Exception ex) when (
                        ex is not OperationCanceledException
                        && !(ex is WebSocketException wse && wse.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely))
                    {
                        // A malformed reassembled message, a garbage ack/control frame, or any
                        // other processing fault for THIS message must not escape the loop and
                        // tear the connection down without a close handshake. Report it as an
                        // error frame and close cleanly.
                        await CloseWithErrorAsync(webSocket, ex, "Message processing failed");
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
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Connection closed",
                            CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        // Already closed, ignore
                    }
                }
            }
        }

        /// <summary>
        /// Sends a generic Error frame (no internal detail) and closes the connection with a
        /// protocol close frame. Used when a single frame cannot be processed (malformed,
        /// oversized, or a garbage control frame) so the failure surfaces to the client as a
        /// clean close rather than an unhandled exception with error-level log noise.
        /// </summary>
        private async Task CloseWithErrorAsync(WebSocket webSocket, Exception ex, string reason)
        {
            _logger.LogWarning(ex, "Closing binary connection: {Reason}", reason);

            await TrySendErrorFrameAsync(
                webSocket, "Malformed or unprocessable frame; closing connection.", null, CancellationToken.None);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.ProtocolError, "Malformed frame");
        }

        /// <summary>
        /// Sends an Error frame, swallowing the WebSocket/cancellation faults that arise when
        /// the socket is already tearing down. Used on the abort paths where a best-effort
        /// notification is wanted but the connection is about to close regardless.
        /// </summary>
        private async Task TrySendErrorFrameAsync(
            WebSocket webSocket, string message, uint? requestId, CancellationToken cancellationToken)
        {
            try
            {
                var errorResponse = new BifrostMessage { Type = BifrostMessageType.Error };
                if (requestId.HasValue)
                    errorResponse.RequestId = requestId.Value;
                errorResponse.Errors.Add(message);
                await SendResponseAsync(webSocket, errorResponse, cancellationToken);
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Closes the socket with the given status/reason, swallowing the WebSocket/cancellation
        /// faults that arise when the socket was already aborted by a cancelled receive.
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

        private async Task ProcessMessageAsync(byte[] messageBytes, BinaryConnectionContext connection)
        {
            BifrostMessage request;
            try
            {
                request = BifrostMessage.FromBytes(messageBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize binary message");
                var errorResponse = new BifrostMessage
                {
                    Type = BifrostMessageType.Error,
                    Errors = { "Server error occurred" },
                };
                await SendResponseAsync(connection.WebSocket, errorResponse, connection.RequestAborted);
                return;
            }

            // A chunked send drains the tail ACK window before completing; if the client
            // pipelined its next request during that drain, the send returns it as a
            // "leftover" frame. Process each in turn until none remain (serial semantics).
            BifrostMessage? next = request;
            while (next != null)
                next = await DispatchAsync(next, connection);
        }

        /// <summary>
        /// Processes a single already-parsed request frame. Returns a leftover frame the
        /// client pipelined (surfaced by the chunked-send tail drain) that must be processed
        /// next, or null when nothing further is pending.
        /// </summary>
        private async Task<BifrostMessage?> DispatchAsync(BifrostMessage request, BinaryConnectionContext connection)
        {
            // Handle incoming chunk messages from client (large request reassembly).
            // Client chunks carry fragments of a full serialized BifrostMessage;
            // the assembled bytes are deserialized to recover the original Query/Mutation.
            if (request.Type == BifrostMessageType.Chunk)
            {
                var reassembled = await HandleChunkAsync(request, connection);
                if (reassembled == null)
                    return null; // More chunks expected, or reassembly failed.
                request = reassembled;
            }

            return request.Type switch
            {
                // ChunkAck messages are only meaningful during SendChunkedAsync; ignore at top level.
                BifrostMessageType.ChunkAck => null,
                BifrostMessageType.ChunkNack => await HandleNackAsync(request, connection),
                BifrostMessageType.Resume => await HandleResumeAsync(request, connection),
                _ => await HandleQueryAsync(request, connection),
            };
        }

        /// <summary>
        /// Acknowledges an incoming client chunk and adds it to the reassembly buffer. Returns
        /// the reconstructed original request once the final chunk arrives, or null when more
        /// chunks are still expected or reassembly failed (an Error frame is sent in that case).
        /// </summary>
        private async Task<BifrostMessage?> HandleChunkAsync(BifrostMessage request, BinaryConnectionContext connection)
        {
            var ack = ChunkReceiver.CreateAck(request.RequestId, request.ChunkSequence);
            await SendResponseAsync(connection.WebSocket, ack, connection.RequestAborted);

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

            if (assembledBytes == null)
                return null; // More chunks expected

            return BifrostMessage.FromBytes(assembledBytes);
        }

        /// <summary>
        /// ChunkNack at top level: retransmit the requested chunk from this connection's
        /// buffer (never another connection's transfers).
        /// </summary>
        private async Task<BifrostMessage?> HandleNackAsync(BifrostMessage request, BinaryConnectionContext connection)
        {
            var retransmitChunk = connection.ChunkBuffer.TryGet(request.RequestId, request.ChunkSequence);
            if (retransmitChunk != null)
            {
                _logger.LogDebug(
                    "Retransmitting chunk {Sequence} for request {RequestId} (NACK)",
                    request.ChunkSequence, request.RequestId);
                await SendResponseAsync(connection.WebSocket, retransmitChunk, connection.RequestAborted);
            }
            else
            {
                await SendErrorResponseAsync(
                    connection, request.RequestId,
                    $"Chunk {request.ChunkSequence} not available for retransmission");
            }
            return null;
        }

        /// <summary>
        /// Resume: client wants chunks of an interrupted transfer on this connection
        /// retransmitted from last_sequence + 1. The buffer is connection-scoped, so
        /// transfers from other (or previous) connections are never resumable here.
        /// </summary>
        private async Task<BifrostMessage?> HandleResumeAsync(BifrostMessage request, BinaryConnectionContext connection)
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
                await SendResponseAsync(connection.WebSocket, resumeAck, connection.RequestAborted);

                return await SendChunksWithAbortAsync(connection, request.RequestId,
                    () => connection.ChunkSender.SendChunksAsync(
                        connection.WebSocket, remainingChunks, 0, connection.ReceiveBuffer, connection.RequestAborted));
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
            await SendResponseAsync(connection.WebSocket, emptyResumeAck, connection.RequestAborted);
            return null;
        }

        /// <summary>
        /// Executes a Query/Mutation request and sends the response, chunking it when it
        /// exceeds the chunk threshold.
        /// </summary>
        private async Task<BifrostMessage?> HandleQueryAsync(BifrostMessage request, BinaryConnectionContext connection)
        {
            var response = await ExecuteRequestAsync(request, connection.HttpContext);

            if (connection.ChunkSender.RequiresChunking(response))
            {
                _logger.LogDebug(
                    "Chunking response for request {RequestId}: {PayloadSize} bytes",
                    response.RequestId, response.Payload.Length);
                return await SendChunksWithAbortAsync(connection, response.RequestId,
                    () => connection.ChunkSender.SendChunkedAsync(
                        connection.WebSocket, response, connection.ReceiveBuffer, connection.RequestAborted));
            }

            await SendResponseAsync(connection.WebSocket, response, connection.RequestAborted);
            return null;
        }

        /// <summary>
        /// Runs a chunked send, releasing the buffered chunks on full delivery (tail window
        /// drained) instead of letting the whole payload linger until TTL expiry, and aborting
        /// the transfer on an ack timeout or a per-sequence retransmit-limit breach. Returns any
        /// leftover pipelined frame surfaced by the send, or null when the transfer aborted.
        /// </summary>
        private async Task<BifrostMessage?> SendChunksWithAbortAsync(
            BinaryConnectionContext connection, uint requestId, Func<Task<BifrostMessage?>> send)
        {
            try
            {
                var leftover = await send();
                connection.ChunkBuffer.Complete(requestId);
                return leftover;
            }
            catch (TimeoutException ex)
            {
                await AbortOnAckTimeoutAsync(connection.WebSocket, requestId, ex);
            }
            catch (ChunkRetransmitLimitExceededException ex)
            {
                await AbortOnRetransmitLimitAsync(connection.WebSocket, requestId, ex, connection.RequestAborted);
            }
            return null;
        }

        private static Task SendErrorResponseAsync(BinaryConnectionContext connection, uint requestId, string message)
        {
            var errorResponse = new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.Error,
            };
            errorResponse.Errors.Add(message);
            return SendResponseAsync(connection.WebSocket, errorResponse, connection.RequestAborted);
        }

        /// <summary>
        /// Handles a chunk-acknowledgement timeout: the client stopped acking mid-transfer,
        /// so the transfer is aborted and the connection closed rather than letting the
        /// send loop pin the connection indefinitely.
        /// </summary>
        private async Task AbortOnAckTimeoutAsync(WebSocket webSocket, uint requestId, TimeoutException ex)
        {
            _logger.LogWarning(
                ex, "Chunk acknowledgement timeout for request {RequestId}; closing connection", requestId);
            // Socket may already be aborted by the cancelled receive; TryCloseAsync swallows that.
            await TryCloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "Chunk acknowledgement timeout");
        }

        /// <summary>
        /// Handles a per-sequence retransmit-limit breach: a client that perpetually NACKs
        /// the same chunk. The transfer is aborted; the client is told why via an Error
        /// frame before the connection is closed, so it surfaces a real failure instead of
        /// hanging.
        /// </summary>
        private async Task AbortOnRetransmitLimitAsync(
            WebSocket webSocket,
            uint requestId,
            ChunkRetransmitLimitExceededException ex,
            CancellationToken cancellationToken)
        {
            _logger.LogWarning(
                ex, "Chunk retransmission limit exceeded for request {RequestId}; aborting transfer", requestId);

            await TrySendErrorFrameAsync(
                webSocket, "Chunk retransmission limit exceeded; transfer aborted.", requestId, cancellationToken);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "Chunk retransmission limit exceeded");
        }

        private async Task<BifrostMessage> ExecuteRequestAsync(BifrostMessage request, HttpContext httpContext)
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

            IReadOnlyDictionary<string, object?>? variables;
            try
            {
                variables = ParseVariables(request.VariablesJson);
            }
            catch (JsonException)
            {
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Invalid variables JSON");
                return response;
            }

            try
            {
                var bifrostRequest = new BifrostRequest
                {
                    Query = request.Query,
                    Variables = variables,
                    UserContext = BifrostAuthContextFactory.Resolve(httpContext).CreateUserContext(httpContext),
                    RequestServices = httpContext.RequestServices,
                    CancellationToken = httpContext.RequestAborted,
                };

                var result = await _engine.ExecuteAsync(bifrostRequest, _endpointPath);

                if (!result.IsSuccess)
                {
                    foreach (var error in result.Errors)
                        response.Errors.Add(error.Message);
                }

                if (result.Data != null)
                {
                    response.Payload = await SerializeResultPayloadAsync(result, httpContext);
                }
            }
            catch (UnmappedOidcIssuerException)
            {
                // Token from an OIDC issuer this deployment has not mapped — fail closed
                // instead of degrading the principal through the local claim path.
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Forbidden: unrecognized token issuer");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing binary request {RequestId}", request.RequestId);
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Query execution failed");
            }

            return response;
        }

        /// <summary>
        /// Serializes the execution result's data into the binary Payload using the same
        /// registered <see cref="IGraphQLSerializer"/> the HTTP path (GraphQLFrontend) uses.
        /// <see cref="BifrostResult.Data"/> is a GraphQL.NET ExecutionNode graph that only the
        /// registered serializer renders into the correct GraphQL wire shape
        /// (<c>{"data":{...}}</c>); a bare System.Text.Json pass would emit the node internals.
        /// Falls back to System.Text.Json only when no serializer is resolvable (e.g. a unit
        /// test with no DI container), where the data is already a plain object graph.
        /// </summary>
        private static async Task<byte[]> SerializeResultPayloadAsync(BifrostResult result, HttpContext httpContext)
        {
            var serializer = httpContext.RequestServices?.GetService<IGraphQLSerializer>();
            if (serializer == null)
                return JsonSerializer.SerializeToUtf8Bytes(result.Data);

            // Executed must be true or the GraphQL serializer omits the "data" field entirely.
            var executionResult = new ExecutionResult { Data = result.Data, Executed = true };
            if (result.Errors.Count > 0)
            {
                var errors = new ExecutionErrors();
                foreach (var error in result.Errors)
                    errors.Add(new ExecutionError(error.Message));
                executionResult.Errors = errors;
            }

            using var ms = new MemoryStream();
            await serializer.WriteAsync(ms, executionResult, httpContext.RequestAborted);
            return ms.ToArray();
        }

        private static IReadOnlyDictionary<string, object?>? ParseVariables(string variablesJson)
        {
            if (string.IsNullOrEmpty(variablesJson))
                return null;

            // Malformed variables JSON must surface as an error rather than silently
            // executing the operation with no variables (which would run against
            // wrong/default inputs). The caller maps JsonException to an Error reply.
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(variablesJson);
        }

        private static async Task SendResponseAsync(WebSocket webSocket, BifrostMessage response, CancellationToken cancellationToken)
        {
            var bytes = response.ToBytes();
            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }

        /// <summary>
        /// Reads a complete WebSocket message, handling multi-fragment messages.
        /// Returns the assembled message bytes and the message type.
        /// </summary>
        private static async Task<(byte[] data, WebSocketMessageType type)> ReadFullMessageAsync(
            WebSocket webSocket, byte[] buffer, CancellationToken cancellationToken)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return (Array.Empty<byte>(), WebSocketMessageType.Close);

            if (result.EndOfMessage)
            {
                // Single-frame message (common case): avoid allocation of a second buffer
                var data = new byte[result.Count];
                Buffer.BlockCopy(buffer, 0, data, 0, result.Count);
                return (data, result.MessageType);
            }

            // Multi-frame message: accumulate into a MemoryStream
            using var ms = new MemoryStream();
            ms.Write(buffer, 0, result.Count);

            while (!result.EndOfMessage)
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    return (Array.Empty<byte>(), WebSocketMessageType.Close);

                ms.Write(buffer, 0, result.Count);

                if (ms.Length > MaxFrameSize)
                    throw new InvalidOperationException("Message exceeds maximum size of " + MaxFrameSize + " bytes");
            }

            return (ms.ToArray(), result.MessageType);
        }
    }
}

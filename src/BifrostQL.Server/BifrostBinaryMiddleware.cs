using System.Net.WebSockets;
using System.Text.Json;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// ASP.NET Core middleware that serves BifrostQL queries over WebSocket binary frames.
    /// Accepts WebSocket upgrade requests at the configured path, reads protobuf-encoded
    /// BifrostMessage envelopes from binary frames, executes them via IBifrostEngine,
    /// and sends protobuf-encoded responses back as binary frames.
    ///
    /// Supports connection multiplexing via request_id: multiple in-flight requests
    /// share a single WebSocket connection, with responses matched by request_id.
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
            TimeSpan ackTimeout)
        {
            _next = next;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _chunkThreshold = chunkThreshold;
            _ackWindow = ackWindow;
            _ackTimeout = ackTimeout;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await _next(context);
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await HandleConnectionAsync(webSocket, context);
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

            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var (messageBytes, messageType) = await ReadFullMessageAsync(webSocket, buffer, httpContext.RequestAborted);

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

                    await ProcessMessageAsync(messageBytes, webSocket, httpContext, buffer, chunkReceiver, chunkSender, chunkBuffer);
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

        private async Task ProcessMessageAsync(
            byte[] messageBytes,
            WebSocket webSocket,
            HttpContext httpContext,
            byte[] receiveBuffer,
            ChunkReceiver chunkReceiver,
            ChunkSender chunkSender,
            ChunkBuffer chunkBuffer)
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
                await SendResponseAsync(webSocket, errorResponse, httpContext.RequestAborted);
                return;
            }

            // Handle incoming chunk messages from client (large request reassembly).
            // Client chunks carry fragments of a full serialized BifrostMessage;
            // the assembled bytes are deserialized to recover the original Query/Mutation.
            if (request.Type == BifrostMessageType.Chunk)
            {
                var ack = ChunkReceiver.CreateAck(request.RequestId, request.ChunkSequence);
                await SendResponseAsync(webSocket, ack, httpContext.RequestAborted);

                byte[]? assembledBytes;
                try
                {
                    assembledBytes = chunkReceiver.AddChunk(request);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Chunk reassembly failed for request {RequestId}", request.RequestId);
                    var errorResponse = new BifrostMessage
                    {
                        RequestId = request.RequestId,
                        Type = BifrostMessageType.Error,
                        Errors = { "Invalid chunk" },
                    };
                    await SendResponseAsync(webSocket, errorResponse, httpContext.RequestAborted);
                    return;
                }

                if (assembledBytes == null)
                    return; // More chunks expected

                request = BifrostMessage.FromBytes(assembledBytes);
            }

            // ChunkAck messages are only meaningful during SendChunkedAsync; ignore at top level
            if (request.Type == BifrostMessageType.ChunkAck)
                return;

            // ChunkNack at top level: retransmit the requested chunk from this
            // connection's buffer (never another connection's transfers).
            if (request.Type == BifrostMessageType.ChunkNack)
            {
                var retransmitChunk = chunkBuffer.TryGet(request.RequestId, request.ChunkSequence);
                if (retransmitChunk != null)
                {
                    _logger.LogDebug(
                        "Retransmitting chunk {Sequence} for request {RequestId} (NACK)",
                        request.ChunkSequence, request.RequestId);
                    await SendResponseAsync(webSocket, retransmitChunk, httpContext.RequestAborted);
                }
                else
                {
                    var errorResponse = new BifrostMessage
                    {
                        RequestId = request.RequestId,
                        Type = BifrostMessageType.Error,
                        Errors = { $"Chunk {request.ChunkSequence} not available for retransmission" },
                    };
                    await SendResponseAsync(webSocket, errorResponse, httpContext.RequestAborted);
                }
                return;
            }

            // Resume: client wants chunks of an interrupted transfer on this connection
            // retransmitted from last_sequence + 1. The buffer is connection-scoped, so
            // transfers from other (or previous) connections are never resumable here.
            if (request.Type == BifrostMessageType.Resume)
            {
                var remainingChunks = chunkBuffer.GetChunksAfter(request.RequestId, request.LastSequence);
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
                    await SendResponseAsync(webSocket, resumeAck, httpContext.RequestAborted);

                    try
                    {
                        await chunkSender.SendChunksAsync(
                            webSocket, remainingChunks, 0, receiveBuffer, httpContext.RequestAborted);
                        chunkBuffer.Complete(request.RequestId);
                    }
                    catch (TimeoutException ex)
                    {
                        await AbortOnAckTimeoutAsync(webSocket, request.RequestId, ex);
                    }
                }
                else
                {
                    // Transfer expired or unknown: send ResumeAck with 0 chunks
                    _logger.LogDebug(
                        "Resume request {RequestId}: no chunks available (expired or unknown)",
                        request.RequestId);
                    var resumeAck = new BifrostMessage
                    {
                        RequestId = request.RequestId,
                        Type = BifrostMessageType.ResumeAck,
                        ChunkTotal = 0,
                        LastSequence = request.LastSequence,
                    };
                    await SendResponseAsync(webSocket, resumeAck, httpContext.RequestAborted);
                }
                return;
            }

            var response = await ExecuteRequestAsync(request, httpContext);

            if (chunkSender.RequiresChunking(response))
            {
                _logger.LogDebug(
                    "Chunking response for request {RequestId}: {PayloadSize} bytes",
                    response.RequestId, response.Payload.Length);
                try
                {
                    await chunkSender.SendChunkedAsync(webSocket, response, receiveBuffer, httpContext.RequestAborted);
                    // Transfer delivered in full: release the buffered chunks now instead
                    // of letting the whole payload linger until TTL expiry.
                    chunkBuffer.Complete(response.RequestId);
                }
                catch (TimeoutException ex)
                {
                    await AbortOnAckTimeoutAsync(webSocket, response.RequestId, ex);
                }
            }
            else
            {
                await SendResponseAsync(webSocket, response, httpContext.RequestAborted);
            }
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
            try
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Chunk acknowledgement timeout",
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Socket already aborted by the cancelled receive; nothing more to do.
            }
            catch (OperationCanceledException)
            {
            }
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
                    UserContext = BuildUserContext(httpContext),
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
                    response.Payload = JsonSerializer.SerializeToUtf8Bytes(result.Data);
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

        private static IReadOnlyDictionary<string, object?>? ParseVariables(string variablesJson)
        {
            if (string.IsNullOrEmpty(variablesJson))
                return null;

            // Malformed variables JSON must surface as an error rather than silently
            // executing the operation with no variables (which would run against
            // wrong/default inputs). The caller maps JsonException to an Error reply.
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(variablesJson);
        }

        private static IDictionary<string, object?> BuildUserContext(HttpContext context)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
                return new BifrostContext(context);

            return new Dictionary<string, object?>();
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

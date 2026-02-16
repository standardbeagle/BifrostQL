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
    /// </summary>
    public sealed class BifrostBinaryMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IBifrostEngine _engine;
        private readonly string _endpointPath;
        private readonly ILogger<BifrostBinaryMiddleware> _logger;

        /// <summary>
        /// Maximum binary frame size (4 MB). Messages larger than this are rejected.
        /// </summary>
        private const int MaxFrameSize = 4 * 1024 * 1024;

        public BifrostBinaryMiddleware(
            RequestDelegate next,
            IBifrostEngine engine,
            string endpointPath,
            ILogger<BifrostBinaryMiddleware> logger)
        {
            _next = next;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var (messageBytes, messageType) = await ReadFullMessageAsync(webSocket, buffer, httpContext.RequestAborted);

                    if (messageType == WebSocketMessageType.Close)
                        break;

                    if (messageType != WebSocketMessageType.Binary)
                    {
                        // Only binary frames supported; close with protocol error
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "Only binary frames are supported",
                            CancellationToken.None);
                        break;
                    }

                    // Fire-and-forget for multiplexing: each request is independent.
                    // In a production system this would use a bounded concurrency limiter,
                    // but for Phase 1 basic transport, sequential processing is correct.
                    await ProcessMessageAsync(messageBytes, webSocket, httpContext);
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

        private async Task ProcessMessageAsync(byte[] messageBytes, WebSocket webSocket, HttpContext httpContext)
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
                    Errors = { "Invalid message format: " + ex.Message },
                };
                await SendResponseAsync(webSocket, errorResponse, httpContext.RequestAborted);
                return;
            }

            var response = await ExecuteRequestAsync(request, httpContext);
            await SendResponseAsync(webSocket, response, httpContext.RequestAborted);
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

            try
            {
                var variables = ParseVariables(request.VariablesJson);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing binary request {RequestId}", request.RequestId);
                response.Type = BifrostMessageType.Error;
                response.Errors.Add("Internal server error: " + ex.Message);
            }

            return response;
        }

        private static IReadOnlyDictionary<string, object?>? ParseVariables(string variablesJson)
        {
            if (string.IsNullOrEmpty(variablesJson))
                return null;

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(variablesJson);
                return dict;
            }
            catch (JsonException)
            {
                return null;
            }
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

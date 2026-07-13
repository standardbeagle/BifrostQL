using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// Configuration for the BifrostQL chat endpoints (see <see cref="BifrostChatMiddleware"/>).
    /// </summary>
    public sealed class BifrostChatOptions
    {
        /// <summary>Whether the chat endpoints are enabled. Default: true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Base path the chat endpoints mount under. Default: "/_chat".</summary>
        public string Path { get; set; } = "/_chat";

        /// <summary>
        /// The registered GraphQL endpoint path whose schema (and chat table pair) the
        /// chat endpoints serve. Null uses the single registered endpoint.
        /// </summary>
        public string? GraphQlEndpoint { get; set; }

        /// <summary>
        /// Maximum number of stored messages sent to the model per completion, newest
        /// kept. A conversation longer than this is truncated from the oldest side —
        /// the model never sees the truncated turns. Default: 50.
        /// </summary>
        public int HistoryLimit { get; set; } = 50;

        /// <summary>
        /// Optional system prompt prepended to every completion request. Deployment
        /// configuration only — never client input.
        /// </summary>
        public string? SystemPrompt { get; set; }

        /// <summary>
        /// Maximum model turns per completion when chat connectors expose tools
        /// (see <see cref="ChatCompletionToolOptions.MaxToolIterations"/>). Exceeding
        /// it ends the stream with <c>error {code:"tool-loop-limit"}</c> and persists
        /// no assistant row. Default: 8.
        /// </summary>
        public int MaxToolIterations { get; set; } = ChatCompletionToolOptions.DefaultMaxToolIterations;
    }

    /// <summary>
    /// HTTP front door for the chat module:
    ///
    /// <list type="bullet">
    /// <item><c>POST {Path}/conversations</c> — create a conversation (optional
    /// <c>{"title": …}</c> body), <c>200 {"id": …}</c>.</item>
    /// <item><c>POST {Path}/conversations/{id}/messages</c> — append the caller's
    /// message (<c>{"content": …}</c>) and stream the assistant completion as
    /// <c>text/event-stream</c>: <c>message-accepted</c>, then <c>delta</c> events —
    /// interleaved with <c>tool</c> events (<c>{name, phase, summary}</c>,
    /// <c>phase</c> ∈ <c>call</c>/<c>result</c>) when connector tools run — then
    /// exactly one terminal <c>done</c> or <c>error</c> event.</item>
    /// </list>
    ///
    /// Chat connectors (<see cref="ChatConnectorRegistry"/>) expose tools to the model;
    /// the tool loop runs inside <see cref="IChatCompletionService"/> under the
    /// caller's own auth context. The persistence contract is UNCHANGED by tools: the
    /// assistant row is the final answer text only — the tool transcript (calls,
    /// results, intermediate turns) is streamed live but NOT persisted in this slice.
    /// A tool loop that exceeds <see cref="BifrostChatOptions.MaxToolIterations"/> is
    /// a typed failure: <c>error {code:"tool-loop-limit"}</c>, no assistant row.
    ///
    /// Identity is fail-closed via <see cref="IBifrostAuthContextFactory"/>: an
    /// unauthenticated request is 401 before the body is read or any store/provider
    /// call is made, and every store operation rides the intent executors, so tenant
    /// isolation, policy, crypto, and history hooks apply by construction. A
    /// conversation outside the caller's scope — cross-tenant and nonexistent alike —
    /// answers the same 404. One completion streams per conversation at a time: a
    /// concurrent second POST is 409 (in-process guard; a multi-node deployment needs
    /// an external lock, see the LLM Chat guide).
    ///
    /// Terminal contract: <c>Complete</c>/<c>Truncated</c> persist the assistant text
    /// (truncated text as-is; the stop reason travels only in the <c>done</c> event,
    /// not the row) — <c>Refused</c> persists NOTHING for the assistant, including any
    /// partial deltas already streamed, and ends the stream with
    /// <c>error {code:"refusal"}</c>. A client disconnect cancels the provider stream
    /// and writes no assistant row; the user message always stays once accepted.
    /// Provider failures after the stream started surface as a terminal <c>error</c>
    /// event (the HTTP status is already 200); failures before it map to HTTP codes.
    /// </summary>
    public sealed class BifrostChatMiddleware
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly RequestDelegate _next;
        private readonly BifrostChatOptions _options;
        private readonly ChatConversationStore _store;
        private readonly IQueryIntentExecutor _reads;
        private readonly IChatCompletionService _completions;
        private readonly ChatConnectorRegistry _connectors;
        private readonly ILogger<BifrostChatMiddleware> _logger;
        private readonly ConcurrentDictionary<string, bool> _activeStreams = new();

        public BifrostChatMiddleware(
            RequestDelegate next,
            BifrostChatOptions options,
            IQueryIntentExecutor reads,
            IMutationIntentExecutor writes,
            IChatCompletionService completions,
            ChatConnectorRegistry connectors,
            ILogger<BifrostChatMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _store = new ChatConversationStore(reads, writes, options.GraphQlEndpoint);
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _completions = completions ?? throw new ArgumentNullException(nameof(completions));
            _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments(_options.Path, StringComparison.OrdinalIgnoreCase, out var rest)
                || !TryMatchRoute(rest, out var isCreate, out var conversationId))
            {
                await _next(context);
                return;
            }

            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers.Allow = "POST";
                await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed,
                    "method-not-allowed", "Chat endpoints accept POST only.");
                return;
            }

            // Fail-closed identity gate, BEFORE the body is read and before any
            // store or provider call: anonymous is 401, an unmapped OIDC issuer is
            // 403 (never a degraded identity).
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                    "unauthenticated", "Authentication is required.");
                return;
            }

            IDictionary<string, object?> userContext;
            try
            {
                userContext = BifrostAuthContextFactory.Resolve(context).CreateUserContext(context);
            }
            catch (UnmappedOidcIssuerException)
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                    "forbidden", "The token issuer is not accepted by this deployment.");
                return;
            }

            if (isCreate)
                await CreateConversationAsync(context, userContext);
            else
                await StreamMessageAsync(context, userContext, conversationId!);
        }

        /// <summary>
        /// Matches <c>/conversations</c> (create) and
        /// <c>/conversations/{id}/messages</c> (stream). The id must parse as an
        /// integer — chat primary keys are validated integer identities — and a
        /// malformed id simply falls through to the next middleware (404), the same
        /// outcome as a conversation the caller cannot see.
        /// </summary>
        private static bool TryMatchRoute(PathString rest, out bool isCreate, out object? conversationId)
        {
            isCreate = false;
            conversationId = null;
            var segments = (rest.Value ?? string.Empty).Trim('/').Split('/');

            if (segments is ["conversations"])
            {
                isCreate = true;
                return true;
            }

            if (segments is ["conversations", var id, "messages"]
                && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                conversationId = parsed;
                return true;
            }

            return false;
        }

        private async Task CreateConversationAsync(HttpContext context, IDictionary<string, object?> userContext)
        {
            var (body, bodyError) = await ReadBodyAsync<CreateConversationRequest>(context);
            if (bodyError != null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid-request", bodyError);
                return;
            }

            try
            {
                var id = await _store.CreateConversationAsync(userContext, body?.Title, context.RequestAborted);
                await WriteJsonAsync(context, StatusCodes.Status200OK, new { id });
            }
            catch (BifrostExecutionError ex)
            {
                await WriteExecutionErrorAsync(context, ex);
            }
            catch (InvalidOperationException ex)
            {
                // Chat metadata/config faults (no chat pair, title without a
                // chat-title column) are deployment misconfiguration — loud, not 4xx.
                _logger.LogError(ex, "Chat conversation create failed on configuration.");
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "configuration-error", ex.Message);
            }
        }

        private async Task StreamMessageAsync(
            HttpContext context, IDictionary<string, object?> userContext, object conversationId)
        {
            var cancellation = context.RequestAborted;

            var (body, bodyError) = await ReadBodyAsync<PostMessageRequest>(context);
            if (bodyError != null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid-request", bodyError);
                return;
            }
            if (string.IsNullOrWhiteSpace(body?.Content))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                    "invalid-request", "A non-empty 'content' field is required.");
                return;
            }

            // Visibility BEFORE the stream guard: an out-of-scope caller gets the
            // fail-closed 404 and can never observe 409/stream state for a
            // conversation it cannot see.
            try
            {
                if (!await _store.IsConversationVisibleAsync(userContext, conversationId, cancellation))
                {
                    await WriteNotFoundAsync(context);
                    return;
                }
            }
            catch (BifrostExecutionError ex)
            {
                await WriteExecutionErrorAsync(context, ex);
                return;
            }

            // One stream per conversation at a time, in-process. Held across the
            // user-message append too, so a rejected second POST persists nothing.
            var streamKey = $"{_options.GraphQlEndpoint}|{conversationId}";
            if (!_activeStreams.TryAdd(streamKey, true))
            {
                await WriteErrorAsync(context, StatusCodes.Status409Conflict, "stream-in-progress",
                    "A completion is already streaming for this conversation.");
                return;
            }

            try
            {
                object? userMessageId;
                IReadOnlyList<ChatCompletionMessage> history;
                ChatCompletionRequestOptions? requestOptions;
                try
                {
                    // Connector tools, resolved per request from the cached model and
                    // bound to THIS caller's auth context — a tool call can never run
                    // under any other identity. No connector tables = no tools param.
                    requestOptions = await BuildToolRequestOptionsAsync(userContext);

                    userMessageId = await _store.AppendMessageAsync(
                        userContext, conversationId, ChatMessageRoles.User, body!.Content!, cancellation);
                    history = await _store.ListRecentMessagesAsync(
                        userContext, conversationId, _options.HistoryLimit, cancellation);
                }
                catch (BifrostExecutionError ex)
                {
                    await WriteExecutionErrorAsync(context, ex);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    // Connector misconfiguration (tool-name collisions, invalid tool
                    // names/schemas) is a deployment fault — loud, before any persist.
                    _logger.LogError(ex, "Chat connector tool-set build failed on configuration.");
                    await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                        "configuration-error", ex.Message);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
                {
                    var withPrompt = new List<ChatCompletionMessage>(history.Count + 1)
                    {
                        new(ChatMessageRoles.System, _options.SystemPrompt),
                    };
                    withPrompt.AddRange(history);
                    history = withPrompt;
                }

                await StreamCompletionAsync(
                    context, userContext, conversationId, userMessageId, history, requestOptions, cancellation);
            }
            finally
            {
                _activeStreams.TryRemove(streamKey, out _);
            }
        }

        /// <summary>
        /// Builds the completion's tool options from the registered connectors: the
        /// model's connector tables become tool definitions and the executor is bound
        /// to the caller's auth context. Null when no connector exposes a tool.
        /// </summary>
        private async Task<ChatCompletionRequestOptions?> BuildToolRequestOptionsAsync(
            IDictionary<string, object?> userContext)
        {
            var model = await _reads.GetModelAsync(_options.GraphQlEndpoint);
            var toolSet = _connectors.BuildToolSet(model);
            if (toolSet.IsEmpty)
                return null;

            return new ChatCompletionRequestOptions
            {
                Tools = new ChatCompletionToolOptions
                {
                    Tools = toolSet.Definitions,
                    Executor = toolSet.CreateExecutor(userContext),
                    MaxToolIterations = _options.MaxToolIterations,
                },
            };
        }

        /// <summary>
        /// The SSE phase: from the first byte written the HTTP status is committed to
        /// 200 and every failure must travel as a terminal <c>error</c> event.
        /// </summary>
        private async Task StreamCompletionAsync(
            HttpContext context,
            IDictionary<string, object?> userContext,
            object conversationId,
            object? userMessageId,
            IReadOnlyList<ChatCompletionMessage> history,
            ChatCompletionRequestOptions? requestOptions,
            CancellationToken cancellation)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            try
            {
                await WriteEventAsync(context, "message-accepted", new { userMessageId, conversationId }, cancellation);

                // The completion stream is a cold IAsyncEnumerable — enumerated
                // exactly once, here.
                await foreach (var evt in _completions.StreamAsync(history, requestOptions, cancellation)
                                   .WithCancellation(cancellation))
                {
                    switch (evt)
                    {
                        case ChatCompletionDelta delta:
                            await WriteEventAsync(context, "delta", new { text = delta.Text }, cancellation);
                            break;

                        case ChatToolActivity activity:
                            // Live tool progress, interleaved in stream order. Not
                            // persisted: the assistant row stays final-text-only.
                            await WriteEventAsync(context, "tool", new
                            {
                                name = activity.ToolName,
                                phase = activity.Phase == ChatToolPhase.Call ? "call" : "result",
                                summary = activity.Summary,
                            }, cancellation);
                            break;

                        case ChatCompletionResult { StopReason: ChatCompletionStopReason.Refused } refusal:
                            // Refusal contract: nothing is persisted for the
                            // assistant — not even deltas already streamed.
                            await WriteEventAsync(context, "error", new
                            {
                                code = "refusal",
                                message = "The model declined to answer this request.",
                                refusalCategory = refusal.RefusalCategory,
                            }, cancellation);
                            return;

                        case ChatCompletionResult result:
                            // Complete and Truncated both persist the text as-is;
                            // the stop reason travels only in the done event.
                            var assistantMessageId = await _store.AppendMessageAsync(
                                userContext, conversationId, ChatMessageRoles.Assistant, result.FullText, cancellation);
                            await WriteEventAsync(context, "done", new
                            {
                                assistantMessageId,
                                stopReason = StopReasonToken(result.StopReason),
                            }, cancellation);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Client disconnected: the same token cancelled the provider stream;
                // no assistant row is written and the user message stays.
            }
            catch (ChatToolLoopLimitException ex)
            {
                // The tool loop never converged: typed failure, nothing persisted for
                // the assistant (deltas already streamed are display-only).
                await TryWriteEventAsync(context, "error",
                    new { code = "tool-loop-limit", message = ex.Message });
            }
            catch (ChatCompletionException ex)
            {
                await TryWriteEventAsync(context, "error",
                    new { code = "provider-error", message = ex.Message, retryable = ex.Retryable });
            }
            catch (ChatConversationNotFoundException)
            {
                // The conversation vanished mid-stream (deleted under the caller).
                await TryWriteEventAsync(context, "error",
                    new { code = "not-found", message = "Conversation not found." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat completion stream for conversation {ConversationId} failed.", conversationId);
                await TryWriteEventAsync(context, "error",
                    new { code = "internal-error", message = "An unexpected error occurred." });
            }
        }

        private static string StopReasonToken(ChatCompletionStopReason stopReason) => stopReason switch
        {
            ChatCompletionStopReason.Complete => "complete",
            ChatCompletionStopReason.Truncated => "truncated",
            _ => throw new ArgumentOutOfRangeException(nameof(stopReason), stopReason,
                "Refused never reaches the done event; any other value is a contract violation."),
        };

        private static async Task WriteEventAsync(HttpContext context, string eventName, object payload, CancellationToken cancellation)
        {
            var frame = ServerSentEvents.Format(eventName, JsonSerializer.Serialize(payload, Json));
            await context.Response.WriteAsync(frame, Encoding.UTF8, cancellation);
            await context.Response.Body.FlushAsync(cancellation);
        }

        /// <summary>Best-effort terminal error event: the client may already be gone.</summary>
        private async Task TryWriteEventAsync(HttpContext context, string eventName, object payload)
        {
            try
            {
                await WriteEventAsync(context, eventName, payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not deliver the terminal chat '{Event}' event.", eventName);
            }
        }

        /// <summary>
        /// Reads the request body as JSON. An empty body reads as a null body (the
        /// create route's title is optional); malformed JSON reports the caller error.
        /// </summary>
        private static async Task<(T? Body, string? Error)> ReadBodyAsync<T>(HttpContext context) where T : class
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(context.RequestAborted);
            if (string.IsNullOrWhiteSpace(raw))
                return (null, null);
            try
            {
                return (JsonSerializer.Deserialize<T>(raw, Json), null);
            }
            catch (JsonException)
            {
                return (null, "The request body is not valid JSON.");
            }
        }

        private static Task WriteNotFoundAsync(HttpContext context) =>
            WriteErrorAsync(context, StatusCodes.Status404NotFound, "not-found", "Conversation not found.");

        /// <summary>
        /// Maps store failures raised BEFORE the SSE stream started to HTTP codes:
        /// the typed not-found is 404 (cross-tenant and nonexistent identical), an
        /// authorization denial (tenant/policy) is 403, anything else is 500 with the
        /// error's authored, transport-safe message.
        /// </summary>
        private static Task WriteExecutionErrorAsync(HttpContext context, BifrostExecutionError ex) => ex switch
        {
            ChatConversationNotFoundException => WriteNotFoundAsync(context),
            _ when ex.ErrorCode == BifrostExecutionError.AccessDeniedCode =>
                WriteErrorAsync(context, StatusCodes.Status403Forbidden, "denied", ex.Message),
            _ => WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "execution-error", ex.Message),
        };

        private static Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message) =>
            WriteJsonAsync(context, statusCode, new { code, message });

        private static async Task WriteJsonAsync(HttpContext context, int statusCode, object payload)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(payload, Json), context.RequestAborted);
        }

        private sealed record CreateConversationRequest(string? Title);

        private sealed record PostMessageRequest(string? Content);
    }
}

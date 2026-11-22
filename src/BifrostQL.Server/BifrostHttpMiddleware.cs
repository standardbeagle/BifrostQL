using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.OperationId;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Transport;
using GraphQL.Types;

namespace BifrostQL.Server
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class BifrostHttpMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IGraphQLSerializer _serializer;
        private readonly BifrostDocumentExecutor _executor;
        private readonly ILogger<BifrostHttpMiddleware> _logger;

        public BifrostHttpMiddleware(
            RequestDelegate next,
            IGraphQLSerializer serializer,
            IDocumentExecuter documentExecutor,
            ILogger<BifrostHttpMiddleware> logger)
        {
            _next = next;
            _serializer = serializer;
            _executor = new BifrostDocumentExecutor(documentExecutor);
            _logger = logger;
            // Wire the shared sanitized-error sink once so lookup misses on the
            // GraphQL path (query-model parse, generic/file resolvers) log their
            // raw detail server-side. Core construction paths have no DI, so the
            // seam is static; first host to build wins, later sets are no-ops.
            BifrostErrorSink.Attach(logger);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsGraphQLRequest(context.Request))
            {
                await _next(context);
                return;
            }

            // One funnel for body parse + identity projection + execution (M13): every
            // condition maps to a GraphQL-shaped error with a status of its own — 400 for a
            // malformed body, 403 for an unprojectable identity — and no exception text ever
            // reaches the wire (.claude/rules/protocol-adapter-security.md invariants 3/10).
            GraphQLRequest? request;
            try
            {
                request = await _serializer.ReadAsync<GraphQLRequest>(context.Request.Body, context.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "GraphQL request body could not be parsed.");
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                    "The request body could not be parsed.");
                return;
            }
            if (request == null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                    "The request body could not be parsed.");
                return;
            }

            // The shared gate is the ONLY identity projection on this mount. Anonymous stays
            // anonymous here (an empty user context): per-endpoint auth enforcement lives in
            // the branch gate UseBifrostEndpoints installs, not in this middleware. A FAULTED
            // projection (unmapped OIDC issuer, subject-less principal) is refused here and
            // never escapes to the host as an unhandled fault.
            var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(context);
            if (outcome == BifrostIdentityOutcome.Unprojectable)
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                    "The caller identity is not accepted by this deployment.");
                return;
            }

            // The Idempotency-Key header reaches the resolvers only through this one key, which
            // is overwritten or removed on every request so no identity claim can supply it.
            userContext.Remove(OperationIdConfig.UserContextKey);
            if (context.Request.Headers.TryGetValue(OperationIdConfig.Header, out var operationIds))
            {
                if (operationIds.Count != 1)
                {
                    await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                        "The Idempotency-Key header must carry exactly one value.");
                    return;
                }
                userContext[OperationIdConfig.UserContextKey] = operationIds[0];
            }

            var options = new ExecutionOptions
            {
                Query = request.Query,
                Variables = request.Variables,
                OperationName = request.OperationName,
                Extensions = request.Extensions,
                RequestServices = context.RequestServices,
                CancellationToken = context.RequestAborted,
                UserContext = userContext,
            };

            ExecutionResult result;
            try
            {
                result = await _executor.ExecuteAsync(options);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client disconnected mid-execution — routine, not a server fault.
                // One quiet line, no stack trace; the response can't be delivered anyway.
                _logger.LogDebug("GraphQL request canceled: client disconnected.");
                return;
            }
            catch (Exception ex) when (StatementTimeoutOf(ex, context.RequestAborted) is { } timeout)
            {
                throw new BifrostStatementTimeoutException(timeout);
            }
            catch (Exception ex)
            {
                // Log full exception with stack trace server-side; never expose details to clients.
                _logger.LogError(ex, "GraphQL execution failed");

                result = new ExecutionResult
                {
                    Errors = new ExecutionErrors
                    {
                        new ExecutionError("An unexpected server error occurred.")
                    }
                };
            }

            // A statement the database cancelled at its own time limit is the host's timeout, not
            // a GraphQL error: its transaction has rolled back, nothing is written yet, and the
            // host's middleware answers it (503, say). Thrown for any field of the document, so a
            // sibling field's committed write is reported by the host, not by this body.
            if (result.Errors is { } errors)
            {
                foreach (var error in errors)
                {
                    if (StatementTimeoutOf(error, context.RequestAborted) is { } timeout)
                        throw new BifrostStatementTimeoutException(timeout);
                }
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 200;
            await _serializer.WriteAsync(context.Response.Body, result, context.RequestAborted);
        }

        /// <summary>
        /// The exception in <paramref name="ex"/>'s chain that says the database ended a statement
        /// at its time limit (<see cref="BifrostExecutionError.IsStatementTimeout"/>), or null. A
        /// request whose own token is cancelled has none: its <c>57014</c> is the client leaving.
        /// </summary>
        private static Exception? StatementTimeoutOf(Exception ex, CancellationToken requestAborted)
            => !requestAborted.IsCancellationRequested && BifrostExecutionError.IsStatementTimeout(ex) ? ex : null;

        /// <summary>
        /// The one error writer for this mount's funnel: a GraphQL-shaped
        /// <c>{"errors":[...]}</c> body under a condition-mapped status, carrying only the
        /// generic message — never exception text
        /// (.claude/rules/protocol-adapter-security.md invariant 3).
        /// </summary>
        private async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = statusCode;
            var result = new ExecutionResult
            {
                Errors = new ExecutionErrors { new ExecutionError(message) },
            };
            await _serializer.WriteAsync(context.Response.Body, result, context.RequestAborted);
        }

        private static bool IsGraphQLRequest(HttpRequest request)
        {
            return string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && request.ContentType != null
                && request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Raised when a GraphQL request path matches no registered endpoint in a deployment
    /// that has more than one. Prevents an unrouted path from silently resolving to the
    /// first registered database's schema.
    /// </summary>
    internal sealed class UnknownBifrostEndpointException : Exception
    {
        public UnknownBifrostEndpointException(string? pathBase, string? path)
            : base($"No BifrostQL endpoint registered for pathBase='{pathBase}' path='{path}'.")
        {
        }
    }

    public class BifrostDocumentExecutor : IDocumentExecuter
    {
        private readonly IDocumentExecuter _documentExecutor;
        public BifrostDocumentExecutor(IDocumentExecuter documentExecutor)
        {
            _documentExecutor = documentExecutor ?? throw new ArgumentNullException(nameof(documentExecutor));
        }

        public async Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            var services = options.RequestServices!;
            var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
            var extensionsLoader = services.GetRequiredService<PathCache<Inputs>>();
            // app.Map() strips the matched prefix from Path and moves it to PathBase, so the
            // endpoint is looked up by PathBase before Path (ResolveExtensionsAsync).
            var userContext = options.UserContext as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            var (setup, error) = await BifrostExecutionPipeline.PrepareAsync(
                services, context,
                () => ResolveExtensionsAsync(extensionsLoader, context),
                userContext,
                context?.TraceIdentifier ?? Guid.NewGuid().ToString("N"));
            if (setup is null)
                return new ExecutionResult { Errors = new ExecutionErrors { new ExecutionError(error!) } };

            options.Schema = setup.Schema;
            options.Extensions = setup.Extensions;
            return await _documentExecutor.ExecuteAsync(options);
        }

        internal static async Task<Inputs> ResolveExtensionsAsync(PathCache<Inputs> cache, HttpContext? context)
        {
            if (context?.Request == null)
                throw new ArgumentNullException("context", "HttpContext.Request is null");

            // When using app.Map(), the matched path moves to PathBase and Path becomes the remainder.
            // Try PathBase first (where app.Map puts the endpoint path), then Path, then first registered.
            // The PathCache is ordinal-keyed while app.Map matches case-insensitively, so a
            // request to /GraphQL/sales arrives with that casing but the loader is registered
            // under the lowercase key. Normalize the lookup key so mixed-case paths resolve.
            var pathBase = NormalizePathKey(context.Request.PathBase.Value);
            if (!string.IsNullOrEmpty(pathBase) && cache.HasPath(pathBase))
                return await cache.GetValueAsync(pathBase);

            var path = NormalizePathKey(context.Request.Path.Value);
            if (!string.IsNullOrEmpty(path) && cache.HasPath(path))
                return await cache.GetValueAsync(path);

            // No explicit path match. Falling back to the first registered endpoint is only
            // safe with a single endpoint (single-DB deployment). With multiple endpoints,
            // serving the first DB's schema for an unmatched path silently answers the query
            // against the wrong database — reject instead so a caller can't cross DB
            // boundaries by hitting an unrouted path.
            if (cache.Count > 1)
                throw new UnknownBifrostEndpointException(pathBase, path);

            return await cache.GetFirstValueAsync()
                ?? throw new InvalidOperationException("No BifrostQL schemas are configured. Set a connection string first.");
        }

        /// <summary>
        /// Normalizes a request path to the lowercase key form under which endpoint loaders
        /// are registered. app.Map matches case-insensitively but PathCache keys ordinally,
        /// so this bridges the two without editing Core/PathCache.
        /// </summary>
        private static string? NormalizePathKey(string? path)
            => string.IsNullOrEmpty(path) ? path : path.ToLowerInvariant();

        public Inputs Combine(IReadOnlyDictionary<string, object?> input1, IReadOnlyDictionary<string, object?> input2)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in input1)
            {
                dict[kv.Key] = kv.Value;
            }
            // Values from input2 override input1 (e.g. the per-profile "model").
            foreach (var kv in input2)
            {
                dict[kv.Key] = kv.Value;
            }
            return new Inputs(dict);
        }
    }
}

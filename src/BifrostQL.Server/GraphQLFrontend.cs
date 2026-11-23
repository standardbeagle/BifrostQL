using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Transport;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// GraphQL protocol frontend. Parses JSON-encoded GraphQL requests and serializes
    /// GraphQL JSON responses. This wraps the existing BifrostQL GraphQL behavior
    /// behind the IProtocolFrontend contract.
    /// </summary>
    public sealed class GraphQLFrontend : IProtocolFrontend
    {
        private readonly IGraphQLSerializer _serializer;

        public GraphQLFrontend(IGraphQLSerializer serializer)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        }

        public string ProtocolName => "graphql";
        public string ContentType => "application/json";
        public string ResponseContentType => "application/json";

        public async ValueTask<BifrostRequest?> ParseAsync(Stream body, CancellationToken cancellationToken)
        {
            var gqlRequest = await _serializer.ReadAsync<GraphQLRequest>(body, cancellationToken);
            if (gqlRequest == null)
                return null;

            var variables = new Dictionary<string, object?>();
            if (gqlRequest.Variables != null)
            {
                foreach (var kv in gqlRequest.Variables)
                    variables[kv.Key] = kv.Value;
            }

            var extensions = new Dictionary<string, object?>();
            if (gqlRequest.Extensions != null)
            {
                foreach (var kv in gqlRequest.Extensions)
                    extensions[kv.Key] = kv.Value;
            }

            return new BifrostRequest
            {
                Query = gqlRequest.Query ?? "",
                OperationName = gqlRequest.OperationName,
                Variables = variables.Count > 0 ? variables : null,
                Extensions = extensions.Count > 0 ? extensions : null,
                CancellationToken = cancellationToken,
            };
        }

        public async ValueTask SerializeAsync(Stream output, BifrostResult result, CancellationToken cancellationToken)
        {
            // Convert BifrostResult back to GraphQL's ExecutionResult for serialization.
            // This maintains wire-format compatibility with existing GraphQL clients.
            var executionResult = new ExecutionResult
            {
                Data = result.Data,
                // Executed must be true or the serializer omits the "data" field.
                Executed = true,
            };

            if (result.Errors.Count > 0)
            {
                var errors = new ExecutionErrors();
                foreach (var error in result.Errors)
                    errors.Add(new ExecutionError(error.Message));
                executionResult.Errors = errors;
            }

            await _serializer.WriteAsync(output, executionResult, cancellationToken);
        }
    }

    /// <summary>
    /// Protocol-independent execution engine that delegates to GraphQL.NET's document executor.
    /// Handles schema resolution, transformer wiring, and result mapping.
    /// </summary>
    public sealed class BifrostEngine : IBifrostEngine
    {
        private readonly IDocumentExecuter _documentExecuter;
        private readonly PathCache<Inputs> _pathCache;

        /// <summary>
        /// Transformers and observers come from the request's services, filtered by the active
        /// profile (<see cref="BifrostExecutionPipeline"/>), so the engine holds neither.
        /// </summary>
        public BifrostEngine(IDocumentExecuter documentExecuter, PathCache<Inputs> pathCache)
        {
            _documentExecuter = documentExecuter ?? throw new ArgumentNullException(nameof(documentExecuter));
            _pathCache = pathCache ?? throw new ArgumentNullException(nameof(pathCache));
        }

        public async Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
        {
            // The binary transport is mounted at its own path (e.g. /bifrost-ws) which is NOT
            // a registered GraphQL endpoint in the PathCache — only the GraphQL paths (e.g.
            // /graphql) are. Keying the schema lookup by the WebSocket mount path would throw
            // ArgumentOutOfRangeException, surfacing to the client as an opaque "Query
            // execution failed". When the path is not a registered endpoint, resolve the
            // schema from the (single) registered GraphQL endpoint instead.
            // Loaders are registered under a lowercase key (app.Map is case-insensitive but
            // PathCache is ordinal), so normalize before lookup.
            var schemaKey = endpointPath?.ToLowerInvariant() ?? string.Empty;
            Func<Task<Inputs>> loadEndpoint;
            if (_pathCache.HasPath(schemaKey))
            {
                loadEndpoint = () => _pathCache.GetValueAsync(schemaKey);
            }
            else
            {
                // No explicit path match. Falling back to the first registered endpoint is
                // only safe with a single endpoint (single-DB deployment) — the same rule the
                // HTTP path enforces (see BifrostHttpMiddleware, UnknownBifrostEndpointException).
                // With multiple endpoints, "first" is nondeterministic, so refuse instead
                // (returned as a result error, consistent with the profile-gate path below).
                // UseBifrostBinary cannot get here: it refuses an unset or unknown graphqlPath on
                // a multi-endpoint host at mount time (MountAuthRequirement.RequireOneServedEndpoint).
                // What reaches it is a protocol frontend (UseBifrostFrontend / UseBifrostGraphQL)
                // mounted at a path that is not a registered endpoint, so the text names that mount.
                if (_pathCache.Count > 1)
                    return new BifrostResult
                    {
                        Data = null,
                        Errors = new[] { new BifrostResultError
                        {
                            Message = "This mount's path is not a registered GraphQL endpoint and more than one endpoint is " +
                                "registered, so it has no target database. Mount UseBifrostFrontend / UseBifrostGraphQL " +
                                "at the path of the endpoint it serves.",
                        }},
                    };

                loadEndpoint = async () => await _pathCache.GetFirstValueAsync()
                    ?? throw new InvalidOperationException(
                        "No BifrostQL GraphQL endpoint is registered to resolve the schema for the binary transport.");
            }

            var services = request.RequestServices
                ?? throw new InvalidOperationException("BifrostRequest.RequestServices is required.");
            // The binary WebSocket and frontend transports reach execution through this engine.
            // They run the SAME pipeline glue as the HTTP middleware (BifrostExecutionPipeline):
            // profile role gating, per-profile module filtering, and the constant-text error
            // mapping for connection and profile-build faults. HttpContext comes from the same
            // IHttpContextAccessor the HTTP path uses.
            var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
            var userContext = request.UserContext;
            // Every message on a binary socket carries the upgrade request's TraceIdentifier, so
            // each execution gets its own correlation id rather than the socket's.
            var (setup, error) = await BifrostExecutionPipeline.PrepareAsync(
                services, httpContext, loadEndpoint, userContext, Guid.NewGuid().ToString("N"),
                // Wire entries merge with the model in hand: the identity-owned key set (which
                // wire entries may never supply) includes the model's tenant-context-key.
                model => BifrostQL.Core.Auth.WireContextMerger.Merge(userContext, request.WireContext, model));
            if (setup is null)
                return new BifrostResult
                {
                    Data = null,
                    Errors = new[] { new BifrostResultError { Message = error! } },
                };

            var options = new ExecutionOptions
            {
                Query = request.Query,
                OperationName = request.OperationName,
                Variables = request.Variables != null ? new Inputs(new Dictionary<string, object?>(request.Variables)) : null,
                Schema = setup.Schema,
                RequestServices = services,
                CancellationToken = request.CancellationToken,
                UserContext = userContext,
                Extensions = setup.Extensions,
            };

            var gqlResult = await _documentExecuter.ExecuteAsync(options);
            return MapResult(gqlResult);
        }

        private static BifrostResult MapResult(ExecutionResult gqlResult)
        {
            var errors = Array.Empty<BifrostResultError>();

            if (gqlResult.Errors is { Count: > 0 })
            {
                errors = gqlResult.Errors.Select(e => new BifrostResultError
                {
                    Message = e.Message,
                    Path = e.Path?.ToList(),
                    Extensions = WithCode(e),
                }).ToArray();
            }

            return new BifrostResult
            {
                Data = gqlResult.Data,
                Errors = errors,
            };
        }

        /// <summary>
        /// The error's extensions plus its code under <see cref="CodeExtension"/>, as the GraphQL
        /// wire serializes them. GraphQL.NET keeps the code (a
        /// <see cref="BifrostExecutionError.ErrorCode"/> such as <c>OPERATION_ID_REQUIRED</c>)
        /// apart from <see cref="ExecutionError.Extensions"/>, so without this a frontend that
        /// reads the result could not tell one condition from another.
        /// </summary>
        private static IReadOnlyDictionary<string, object?>? WithCode(ExecutionError error)
        {
            if (string.IsNullOrEmpty(error.Code))
                return error.Extensions;
            var extensions = error.Extensions is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(error.Extensions);
            extensions[CodeExtension] = error.Code;
            return extensions;
        }

        /// <summary>The <see cref="BifrostResultError.Extensions"/> key holding the error code.</summary>
        internal const string CodeExtension = "code";
    }
}

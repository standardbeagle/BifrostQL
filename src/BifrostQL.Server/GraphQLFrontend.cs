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
        private readonly IQueryTransformerService _transformerService;
        private readonly IQueryObservers? _observers;

        public BifrostEngine(
            IDocumentExecuter documentExecuter,
            PathCache<Inputs> pathCache,
            IQueryTransformerService transformerService,
            IQueryObservers? observers = null)
        {
            _documentExecuter = documentExecuter ?? throw new ArgumentNullException(nameof(documentExecuter));
            _pathCache = pathCache ?? throw new ArgumentNullException(nameof(pathCache));
            _transformerService = transformerService ?? throw new ArgumentNullException(nameof(transformerService));
            _observers = observers;
        }

        public async Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
        {
            var sharedExtensions = await _pathCache.GetValueAsync(endpointPath);

            var services = request.RequestServices;
            // The binary WebSocket transport reaches execution through this engine. It must
            // enforce the same profile role gating and per-profile module filtering as the
            // HTTP middleware, otherwise a client could use the binary path to bypass
            // profile authorization and — via the fail-closed transformer filter below —
            // tenant isolation and soft-delete. HttpContext is available here through the
            // same IHttpContextAccessor the HTTP path uses (the WebSocket handler runs
            // inside the request's async flow).
            var httpContext = services?.GetService<IHttpContextAccessor>()?.HttpContext;
            var profileRegistry = services?.GetService<BifrostProfileRegistry>();

            var profileResolution = BifrostProfileResolver.Resolve(profileRegistry, httpContext);
            if (profileResolution.HasError)
            {
                return new BifrostResult
                {
                    Data = null,
                    Errors = new[] { new BifrostResultError { Message = profileResolution.ErrorMessage! } },
                };
            }

            var profileName = profileResolution.ProfileName;
            var activeProfile = profileResolution.ActiveProfile;

            // Resolve the model+schema for the active profile so the binary path serves the
            // same per-profile shape as HTTP. Fall back to the base model/schema only when
            // no profile cache is configured (older bootstrap or a direct engine test).
            IDbModel model;
            ISchema schema;
            if (sharedExtensions.TryGetValue("profileModelCache", out var cacheObj) && cacheObj is ProfileModelCache profileCache)
            {
                (model, schema) = profileCache.GetFor(profileName);
            }
            else
            {
                model = (IDbModel)(sharedExtensions["model"]
                    ?? throw new InvalidDataException("model not configured for endpoint: " + endpointPath));
                schema = (ISchema)(sharedExtensions["dbSchema"]
                    ?? throw new InvalidDataException("dbSchema not configured for endpoint: " + endpointPath));
            }

            // Filter transformers/observers by the active profile (fail-closed: security and
            // data-integrity modules always remain). Mirrors BifrostDocumentExecutor so the
            // two transports cannot drift apart.
            var transformerService = _transformerService;
            var filterTransformers = services?.GetService<IFilterTransformers>();
            if (filterTransformers != null)
                transformerService = new QueryTransformerService(BifrostProfileRegistry.FilterBy(filterTransformers, activeProfile));

            var observers = services?.GetService<IQueryObservers>() ?? _observers;
            if (observers != null)
                observers = BifrostProfileRegistry.FilterBy(observers, activeProfile);

            var userContext = request.UserContext;
            if (!userContext.ContainsKey("_correlationId"))
                userContext["_correlationId"] = Guid.NewGuid().ToString("N");
            // Surface the active profile to downstream resolvers, matching the HTTP path.
            userContext[BifrostProfile.UserContextKey] = activeProfile;

            var options = new ExecutionOptions
            {
                Query = request.Query,
                OperationName = request.OperationName,
                Variables = request.Variables != null ? new Inputs(new Dictionary<string, object?>(request.Variables)) : null,
                Schema = schema,
                RequestServices = request.RequestServices,
                CancellationToken = request.CancellationToken,
                UserContext = userContext,
                Extensions = Combine(
                    sharedExtensions,
                    new Dictionary<string, object?>
                    {
                        { "model", model },
                        { "tableReaderFactory", new SqlExecutionManager(model, schema, transformerService, observers) }
                    }),
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
                    Extensions = e.Extensions,
                }).ToArray();
            }

            return new BifrostResult
            {
                Data = gqlResult.Data,
                Errors = errors,
            };
        }

        private static Inputs Combine(IReadOnlyDictionary<string, object?> input1, IReadOnlyDictionary<string, object?> input2)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in input1)
                dict[kv.Key] = kv.Value;
            foreach (var kv in input2)
                dict[kv.Key] = kv.Value;
            return new Inputs(dict);
        }
    }
}

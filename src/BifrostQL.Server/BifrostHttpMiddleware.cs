using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsGraphQLRequest(context.Request))
            {
                await _next(context);
                return;
            }

            var request = await _serializer.ReadAsync<GraphQLRequest>(context.Request.Body, context.RequestAborted);
            if (request == null)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var options = new ExecutionOptions
            {
                Query = request.Query,
                Variables = request.Variables,
                OperationName = request.OperationName,
                Extensions = request.Extensions,
                RequestServices = context.RequestServices,
                CancellationToken = context.RequestAborted,
                UserContext = BuildUserContext(context),
            };

            ExecutionResult result;
            try
            {
                result = await _executor.ExecuteAsync(options);
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

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 200;
            await _serializer.WriteAsync(context.Response.Body, result, context.RequestAborted);
        }

        private static bool IsGraphQLRequest(HttpRequest request)
        {
            return string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && request.ContentType != null
                && request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static IDictionary<string, object?> BuildUserContext(HttpContext context)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
                return new BifrostContext(context);

            return new Dictionary<string, object?>();
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
            var contextAccessor = options.RequestServices!.GetRequiredService<IHttpContextAccessor>();
            var context = contextAccessor.HttpContext;
            var extensionsLoader = options.RequestServices!.GetRequiredService<PathCache<Inputs>>();
            var transformerService = options.RequestServices!.GetRequiredService<IQueryTransformerService>();
            var observers = options.RequestServices!.GetService<IQueryObservers>();

            // Check if a connection string is configured before trying to load the schema
            var setupOptions = options.RequestServices!.GetService<BifrostSetupOptions>();
            if (setupOptions != null && !setupOptions.HasConnectionString)
                return new ExecutionResult { Errors = new ExecutionErrors { new ExecutionError("No database connection configured. Set a connection string first.") } };

            // Resolve the active profile. A null/empty/"default" name resolves to the empty
            // default profile (raw base schema, no opt-in modules). A named profile that is
            // non-null, non-"default", and absent from the registry is an error. Auth/role
            // requirements on a named profile are enforced here.
            var profileRegistry = options.RequestServices!.GetService<BifrostProfileRegistry>();
            var profileResult = ResolveProfile(profileRegistry, context);
            if (profileResult.Error != null)
                return new ExecutionResult { Errors = new ExecutionErrors { profileResult.Error } };
            var profileName = profileResult.ProfileName;
            var activeProfile = profileResult.Profile
                ?? new BifrostProfile { Name = "default", Modules = System.Array.Empty<string>() };

            // app.Map() strips the matched prefix from Path and moves it to PathBase,
            // so we need to check PathBase (where the endpoint path lives after routing)
            // before falling back to Path or the first registered value.
            Inputs sharedExtensions;
            try
            {
                sharedExtensions = await ResolveExtensionsAsync(extensionsLoader, context);
            }
            catch (Exception ex)
            {
                // Surface connection/schema errors as GraphQL errors instead of 500
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                
                // Log full exception with stack trace for debugging
                var logger = options.RequestServices!.GetService<ILogger<BifrostDocumentExecutor>>();
                logger?.LogError(ex, "Schema resolution failed: {Message}", innerMessage);
                
                return new ExecutionResult
                {
                    Errors = new ExecutionErrors
                    {
                        new ExecutionError($"Database connection failed: {innerMessage}")
                    }
                };
            }
            // Build (or fetch cached) the model+schema for the active profile. The empty
            // default profile yields the raw base schema; named profiles fold their own
            // metadata into the model and schema.
            var profileCache = (ProfileModelCache)(sharedExtensions["profileModelCache"]
                ?? throw new InvalidDataException("profileModelCache not configured"));
            var (model, schema) = profileCache.GetFor(profileName);
            options.Schema = schema;

            // Inject correlation ID from ASP.NET Core's TraceIdentifier
            if (options.UserContext is IDictionary<string, object?> userContext && !userContext.ContainsKey("_correlationId"))
                userContext["_correlationId"] = context?.TraceIdentifier ?? Guid.NewGuid().ToString("N");

            // Always filter transformers/observers by the active profile. The empty default
            // profile filters out all opt-in transformers (soft-delete, tenant, etc.).
            var filterTransformers = options.RequestServices!.GetRequiredService<IFilterTransformers>();
            var filteredTransformers = BifrostProfileRegistry.FilterBy(filterTransformers, activeProfile);
            transformerService = new QueryTransformerService(filteredTransformers);
            observers = observers != null ? BifrostProfileRegistry.FilterBy(observers, activeProfile) : null;

            // Store profile in UserContext so downstream resolvers see the active profile.
            if (options.UserContext is IDictionary<string, object?> uc)
                uc[BifrostProfile.UserContextKey] = activeProfile;

            // Override "model" with the per-profile model so request-time readers
            // (IBifrostContext, resolvers) see the same model the schema was built from.
            options.Extensions = Combine(
                sharedExtensions,
                new Dictionary<string, object?>
                {
                    { "model", model },
                    { "tableReaderFactory", new SqlExecutionManager(model, options.Schema, transformerService, observers) },
                }
            );
            return await _documentExecutor.ExecuteAsync(options);
        }

        private static ProfileResolution ResolveProfile(BifrostProfileRegistry? registry, HttpContext? context)
        {
            var profileName = context != null ? ResolveProfileName(context) : null;

            // No name, or the explicit "default" → empty default profile (raw schema).
            if (profileName == null || string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase))
                return new ProfileResolution { ProfileName = profileName };

            // A named profile requires a registry that knows it.
            var profile = registry?.Get(profileName);
            if (profile == null)
                return new ProfileResolution { Error = new ExecutionError($"Unknown profile '{profileName}'.") };

            if (profile.RequireRole != null)
            {
                var user = context!.User;
                if (user?.Identity?.IsAuthenticated != true)
                    return new ProfileResolution { Error = new ExecutionError($"Profile '{profileName}' requires authentication.") };

                if (!user.IsInRole(profile.RequireRole))
                    return new ProfileResolution { Error = new ExecutionError($"Profile '{profileName}' requires role '{profile.RequireRole}'.") };
            }

            return new ProfileResolution { ProfileName = profileName, Profile = profile };
        }

        private static string? ResolveProfileName(HttpContext context)
        {
            // Priority: Header > Query parameter > Path segment
            if (context.Request.Headers.TryGetValue("X-BifrostQL-Profile", out var headerValue)
                && !string.IsNullOrWhiteSpace(headerValue))
            {
                return headerValue.ToString().Trim();
            }

            if (context.Request.Query.TryGetValue("profile", out var queryValue)
                && !string.IsNullOrWhiteSpace(queryValue))
            {
                return queryValue.ToString().Trim();
            }

            // Check for path segment after the mapped endpoint path.
            // After app.Map(), Path contains the remainder (e.g., "/direct" if mapped at "/graphql").
            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path) && path.Length > 1)
            {
                var segment = path.TrimStart('/');
                if (!string.IsNullOrEmpty(segment) && !segment.Contains('/'))
                    return segment;
            }

            return null;
        }

        private readonly struct ProfileResolution
        {
            public string? ProfileName { get; init; }
            public BifrostProfile? Profile { get; init; }
            public ExecutionError? Error { get; init; }
        }

        private static async Task<Inputs> ResolveExtensionsAsync(PathCache<Inputs> cache, HttpContext? context)
        {
            if (context?.Request == null)
                throw new ArgumentNullException("context", "HttpContext.Request is null");

            // When using app.Map(), the matched path moves to PathBase and Path becomes the remainder.
            // Try PathBase first (where app.Map puts the endpoint path), then Path, then first registered.
            var pathBase = context.Request.PathBase.Value;
            if (!string.IsNullOrEmpty(pathBase) && cache.HasPath(pathBase))
                return await cache.GetValueAsync(pathBase);

            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path) && cache.HasPath(path))
                return await cache.GetValueAsync(path);

            return await cache.GetFirstValueAsync()
                ?? throw new InvalidOperationException("No BifrostQL schemas are configured. Set a connection string first.");
        }

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

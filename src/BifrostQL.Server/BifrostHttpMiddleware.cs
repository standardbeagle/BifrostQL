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
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                var errorMessage = $"Server error: {innerMessage}";
                
                // Log full exception with stack trace using ASP.NET Core logging
                _logger.LogError(ex, "GraphQL execution failed: {ErrorMessage}", errorMessage);
                
                result = new ExecutionResult
                {
                    Errors = new ExecutionErrors
                    {
                        new ExecutionError(errorMessage)
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

        public Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            var contextAccessor = options.RequestServices!.GetRequiredService<IHttpContextAccessor>();
            var context = contextAccessor.HttpContext;
            var extensionsLoader = options.RequestServices!.GetRequiredService<PathCache<Inputs>>();
            var transformerService = options.RequestServices!.GetRequiredService<IQueryTransformerService>();
            var observers = options.RequestServices!.GetService<IQueryObservers>();

            // Check if a connection string is configured before trying to load the schema
            var setupOptions = options.RequestServices!.GetService<BifrostSetupOptions>();
            if (setupOptions != null && !setupOptions.HasConnectionString)
                return Task.FromResult(new ExecutionResult { Errors = new ExecutionErrors { new ExecutionError("No database connection configured. Set a connection string first.") } });

            // Resolve profile if configured
            var profileRegistry = options.RequestServices!.GetService<BifrostProfileRegistry>();
            var profileResult = ResolveProfile(profileRegistry, context);
            if (profileResult.Error != null)
                return Task.FromResult(new ExecutionResult { Errors = new ExecutionErrors { profileResult.Error } });

            // app.Map() strips the matched prefix from Path and moves it to PathBase,
            // so we need to check PathBase (where the endpoint path lives after routing)
            // before falling back to Path or the first registered value.
            Inputs sharedExtensions;
            try
            {
                sharedExtensions = ResolveExtensions(extensionsLoader, context);
            }
            catch (Exception ex)
            {
                // Surface connection/schema errors as GraphQL errors instead of 500
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                
                // Log full exception with stack trace for debugging
                var logger = options.RequestServices!.GetService<ILogger<BifrostDocumentExecutor>>();
                logger?.LogError(ex, "Schema resolution failed: {Message}", innerMessage);
                
                return Task.FromResult(new ExecutionResult
                {
                    Errors = new ExecutionErrors
                    {
                        new ExecutionError($"Database connection failed: {innerMessage}")
                    }
                });
            }
            var model = (IDbModel)(sharedExtensions["model"] ?? throw new InvalidDataException("dbSchema not configured"));
            options.Schema = (ISchema)(sharedExtensions["dbSchema"] ?? throw new InvalidDataException("dbSchema not configured"));

            // Inject correlation ID from ASP.NET Core's TraceIdentifier
            if (options.UserContext is IDictionary<string, object?> userContext && !userContext.ContainsKey("_correlationId"))
                userContext["_correlationId"] = context?.TraceIdentifier ?? Guid.NewGuid().ToString("N");

            // Apply profile filtering to transformers and observers
            var activeProfile = profileResult.Profile;
            if (activeProfile != null)
            {
                var filterTransformers = options.RequestServices!.GetRequiredService<IFilterTransformers>();
                var filteredTransformers = BifrostProfileRegistry.FilterBy(filterTransformers, activeProfile);
                transformerService = new QueryTransformerService(filteredTransformers);
                observers = observers != null ? BifrostProfileRegistry.FilterBy(observers, activeProfile) : null;

                // Store profile in UserContext so mutation resolvers can filter their modules
                if (options.UserContext is IDictionary<string, object?> uc)
                    uc[BifrostProfile.UserContextKey] = activeProfile;
            }

            options.Extensions = Combine(
                sharedExtensions,
                new Dictionary<string, object?> { { "tableReaderFactory", new SqlExecutionManager(model, options.Schema, transformerService, observers) } }
            );
            var result = _documentExecutor.ExecuteAsync(options);
            return result;
        }

        private static ProfileResolution ResolveProfile(BifrostProfileRegistry? registry, HttpContext? context)
        {
            if (registry == null || !registry.HasProfiles || context == null)
                return new ProfileResolution();

            var profileName = ResolveProfileName(context);
            if (profileName == null || string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase))
                return new ProfileResolution();

            var profile = registry.Get(profileName);
            if (profile == null)
                return new ProfileResolution { Error = new ExecutionError($"Unknown profile '{profileName}'.") };

            if (profile.RequireRole != null)
            {
                var user = context.User;
                if (user?.Identity?.IsAuthenticated != true)
                    return new ProfileResolution { Error = new ExecutionError($"Profile '{profileName}' requires authentication.") };

                if (!user.IsInRole(profile.RequireRole))
                    return new ProfileResolution { Error = new ExecutionError($"Profile '{profileName}' requires role '{profile.RequireRole}'.") };
            }

            return new ProfileResolution { Profile = profile };
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
            public BifrostProfile? Profile { get; init; }
            public ExecutionError? Error { get; init; }
        }

        private static Inputs ResolveExtensions(PathCache<Inputs> cache, HttpContext? context)
        {
            if (context?.Request == null)
                throw new ArgumentNullException("context", "HttpContext.Request is null");

            // When using app.Map(), the matched path moves to PathBase and Path becomes the remainder.
            // Try PathBase first (where app.Map puts the endpoint path), then Path, then first registered.
            var pathBase = context.Request.PathBase.Value;
            if (!string.IsNullOrEmpty(pathBase) && cache.TryGetValue(pathBase, out var result))
                return result!;

            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path) && cache.TryGetValue(path, out result))
                return result!;

            return cache.GetFirstValue()
                ?? throw new InvalidOperationException("No BifrostQL schemas are configured. Set a connection string first.");
        }

        public Inputs Combine(IReadOnlyDictionary<string, object?> input1, IReadOnlyDictionary<string, object?> input2)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in input1)
            {
                dict.Add(kv.Key, kv.Value);
            }
            foreach (var kv in input2)
            {
                dict.Add(kv.Key, kv.Value);
            }
            return new Inputs(dict);
        }
    }
}

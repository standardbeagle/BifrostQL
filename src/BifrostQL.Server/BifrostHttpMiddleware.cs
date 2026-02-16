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

        public BifrostHttpMiddleware(
            RequestDelegate next,
            IGraphQLSerializer serializer,
            IDocumentExecuter documentExecutor)
        {
            _next = next;
            _serializer = serializer;
            _executor = new BifrostDocumentExecutor(documentExecutor);
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

            var result = await _executor.ExecuteAsync(options);

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

            PathString path = context?.Request?.Path ?? throw new ArgumentNullException("path", "HttpContext.Request has a null path or Request is null");
            var sharedExtensions = extensionsLoader.GetValue(path);
            var model = (IDbModel)(sharedExtensions["model"] ?? throw new InvalidDataException("dbSchema not configured"));
            options.Schema = (ISchema)(sharedExtensions["dbSchema"] ?? throw new InvalidDataException("dbSchema not configured"));

            // Inject correlation ID from ASP.NET Core's TraceIdentifier
            if (options.UserContext is IDictionary<string, object?> userContext && !userContext.ContainsKey("_correlationId"))
                userContext["_correlationId"] = context?.TraceIdentifier ?? Guid.NewGuid().ToString("N");

            options.Extensions = Combine(
                sharedExtensions,
                new Dictionary<string, object?> { { "tableReaderFactory", new SqlExecutionManager(model, options.Schema, transformerService, observers) } }
            );
            var result = _documentExecutor.ExecuteAsync(options);
            return result;
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

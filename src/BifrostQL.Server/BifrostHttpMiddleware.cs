using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types;

namespace BifrostQL.Server
{
    public class BifrostHttpMiddleware : GraphQLHttpMiddleware
    {
        public BifrostHttpMiddleware(
            RequestDelegate next,
            IGraphQLTextSerializer serializer,
            IDocumentExecuter documentExecuter,
            IServiceScopeFactory serviceScopeFactory,
            IHostApplicationLifetime hostApplicationLifetime) :
            base(next,
                serializer,
                new BifrostDocumentExecuter(documentExecuter),
                serviceScopeFactory,
                new GraphQLHttpMiddlewareOptions(),
                hostApplicationLifetime)
        {
        }
    }
    public class BifrostDocumentExecuter : IDocumentExecuter
    {
        private readonly IDocumentExecuter _documentExecuter;
        private static readonly Dictionary<string, ISchema> _schemas = new Dictionary<string, ISchema>();
        public BifrostDocumentExecuter(IDocumentExecuter documentExecuter)
        {
            _documentExecuter = documentExecuter ?? throw new ArgumentNullException(nameof(documentExecuter));
        }

        public static void AddSchema(string path, ISchema schema)
        {
            _schemas.Add(path, schema);
        }

        public Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            var contextAccessor = options.RequestServices!.GetRequiredService<IHttpContextAccessor>();
            var context = contextAccessor.HttpContext;

            if (_schemas.TryGetValue(context?.Request?.Path ?? "", out var schema))
            {
                options.Schema = schema;
            }

            var result = _documentExecuter.ExecuteAsync(options);
            return result;
        }
    }
}

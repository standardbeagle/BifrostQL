using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types;

namespace BifrostQL.Server
{
    public class BifrostHttpMiddleware<T> : GraphQLHttpMiddleware<T> where T : ISchema
    {
        public BifrostHttpMiddleware(
            RequestDelegate next, 
            IGraphQLTextSerializer serializer, 
            IDocumentExecuter<T> documentExecuter, 
            IServiceScopeFactory serviceScopeFactory, 
            IHostApplicationLifetime hostApplicationLifetime) : 
            base(next, 
                serializer, 
                new BifrostDocumentExecuter<T>(documentExecuter), 
                serviceScopeFactory, 
                new GraphQLHttpMiddlewareOptions(), 
                hostApplicationLifetime)
        {
        }
    }

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
    public class BifrostDocumentExecuter<TSchema> : IDocumentExecuter<TSchema> where TSchema : ISchema
    {
        private readonly IDocumentExecuter _documentExecuter;
        public BifrostDocumentExecuter(IDocumentExecuter documentExecuter)
        {
            _documentExecuter = documentExecuter ?? throw new ArgumentNullException(nameof(documentExecuter));
        }

        public Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            if (options.Schema != null)
                throw new InvalidOperationException("ExecutionOptions.Schema must be null when calling this typed IDocumentExecuter<> implementation; it will be pulled from the dependency injection provider.");

            //options.Schema = options.RequestServices!.GetRequiredService<TSchema>();
            var result = _documentExecuter.ExecuteAsync(options);
            return result;
        }
    }

    public class BifrostDocumentExecuter : IDocumentExecuter
    {
        private readonly IDocumentExecuter _documentExecuter;
        public BifrostDocumentExecuter(IDocumentExecuter documentExecuter)
        {
            _documentExecuter = documentExecuter ?? throw new ArgumentNullException(nameof(documentExecuter));
        }

        public Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            if (options.Schema != null)
                throw new InvalidOperationException("ExecutionOptions.Schema must be null when calling this typed IDocumentExecuter<> implementation; it will be pulled from the dependency injection provider.");

            //options.Schema = options.RequestServices!.GetRequiredService<TSchema>();
            var result = _documentExecuter.ExecuteAsync(options);
            return result;
        }
    }
}

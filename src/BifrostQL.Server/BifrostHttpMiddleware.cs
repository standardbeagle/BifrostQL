using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types;
using System.Collections.Generic;

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
            var extensionsLoader = options.RequestServices!.GetRequiredService<PathCache<Inputs>>();

            PathString path = context?.Request?.Path ?? throw new ArgumentNullException("path", "HttpConext.Request has a null path or Request is null");
            var sharedExtensions = extensionsLoader.GetValue(path);
            var model = (IDbModel)(sharedExtensions["model"] ?? throw new InvalidDataException("dbSchema not configured"));
            options.Schema = (ISchema)(sharedExtensions["dbSchema"] ?? throw new InvalidDataException("dbSchema not configured"));

            options.Extensions = Combine(
                sharedExtensions, 
                new Dictionary<string, object?> { { "tableReaderFactory", new TableReaderFactory(model) } }
            );
            var result = _documentExecuter.ExecuteAsync(options);
            return result;
        }

        public Inputs Combine(IReadOnlyDictionary<string, object?> input1, IReadOnlyDictionary<string, object?> input2)
        {
            var dict = new Dictionary<string, object?>();
            foreach(var kv in input1)
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

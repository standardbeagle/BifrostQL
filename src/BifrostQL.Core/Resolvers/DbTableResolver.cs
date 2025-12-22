using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbTableResolver : IFieldResolver
    {

    }

    public class DbTableResolver : IDbTableResolver
    {
        private readonly IDbTable _table;
        public DbTableResolver(IDbTable table)
        {
            _table = table;
        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var factory = (ISqlExecutionManager)(context.InputExtensions["tableReaderFactory"] ?? throw new InvalidDataException("tableReaderFactory not configured"));
            return factory.ResolveAsync(context, _table);
        }
    }

    class TableResult
    {
        public int? Total { get; set; }
        public int? Offset { get; set; }
        public int? Limit { get; set; }
        public object? Data { get; set; }
    }
}

using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbTableResolver : IBifrostResolver, IFieldResolver
    {

    }

    public class DbTableResolver : IDbTableResolver
    {
        private readonly IDbTable _table;
        public DbTableResolver(IDbTable table)
        {
            _table = table;
        }

        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            return bifrost.Executor.ResolveAsync(context, _table);
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
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

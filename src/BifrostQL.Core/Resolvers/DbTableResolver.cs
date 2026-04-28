using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbTableResolver : IBifrostResolver, IFieldResolver
    {
    }

    /// <summary>
    /// Resolver for database table queries.
    /// Delegates execution to the SQL execution manager.
    /// </summary>
    public sealed class DbTableResolver : TableResolverBase, IDbTableResolver
    {
        public DbTableResolver(IDbTable table) : base(table)
        {
        }

        public override ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            return bifrost.Executor.ResolveAsync(context, Table);
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

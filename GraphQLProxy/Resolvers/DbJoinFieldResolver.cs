using GraphQL.Resolvers;
using GraphQL;
using static GraphQLProxy.Resolvers.ReaderEnum;
using GraphQLProxy.Resolvers;

namespace GraphQLProxy
{
    public sealed class DbJoinFieldResolver : IFieldResolver
    {
        private static DbJoinFieldResolver _instance = null!;
        public static DbJoinFieldResolver Instance => _instance ??= new DbJoinFieldResolver();

        private DbJoinFieldResolver()
        {

        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var row = (ReaderCurrent)context.Source!;
            return row.Get(context);
        }
    }

}

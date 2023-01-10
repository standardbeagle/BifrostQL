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
            return context.Source switch
            {
                ReaderCurrent row => row.Get(context),
                SingleRowLookup lookup => lookup.Get(context),
                _ => throw new NotSupportedException()
            } ;
        }
    }

}

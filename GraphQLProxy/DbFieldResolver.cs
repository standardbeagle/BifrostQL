using GraphQL.Resolvers;
using GraphQL;
using static GraphQLProxy.ReaderEnum;

namespace GraphQLProxy
{
    public class DbFieldResolver : IFieldResolver
    {
        private static DbFieldResolver _instance = null!;
        public static DbFieldResolver Instance => _instance ??= new DbFieldResolver();

        private DbFieldResolver()
        {

        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var row = (ReaderCurrent)context.Source!;
            return row.Get(context);
        }
    }

}

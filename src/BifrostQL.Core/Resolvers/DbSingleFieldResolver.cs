using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Resolvers
{
    public sealed class DbSingleFieldResolver : IFieldResolver
    {
        private static DbSingleFieldResolver _instance = null!;
        public static DbSingleFieldResolver Instance => _instance ??= new DbSingleFieldResolver();

        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var row = (ReaderCurrent)context.Source!;
            return row.Get(context);
        }
    }
}

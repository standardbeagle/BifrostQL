using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbJoinFieldResolver : IFieldResolver
    {
        private static DbJoinFieldResolver _instance = null!;
        public static DbJoinFieldResolver Instance => _instance ??= new ();

        private DbJoinFieldResolver()
        {

        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            return context.Source switch
            {
                ReaderCurrent row => row.Get(context),
                SingleRowLookup lookup => lookup.Get(context),
                _ => throw new ExecutionError($"{context?.FieldAst?.Alias?.Name?.StringValue ?? context?.FieldAst?.Name.StringValue ?? "unknown"} has no data associated with it.")
            } ;
        }
    }

}

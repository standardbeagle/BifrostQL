using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbJoinFieldResolver : IBifrostResolver, IFieldResolver
    {
        private static DbJoinFieldResolver _instance = null!;
        public static DbJoinFieldResolver Instance => _instance ??= new();

        private DbJoinFieldResolver()
        {

        }
        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            return context.Source switch
            {
                ReaderCurrent row => row.Get(context),
                SingleRowLookup lookup => lookup.Get(context),
                _ => throw new BifrostExecutionError($"{context.FieldAlias ?? context.FieldName ?? "unknown"} has no data associated with it.")
            };
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }
    }

}

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Bifrost-native resolver contract that replaces GraphQL.NET's IFieldResolver.
    /// Resolvers implement this to handle field resolution without coupling to GraphQL.NET.
    /// </summary>
    public interface IBifrostResolver
    {
        ValueTask<object?> ResolveAsync(IBifrostFieldContext context);
    }
}

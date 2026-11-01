using GraphQL;
using GraphQL.Resolvers;
using GraphQLParser.AST;

namespace BifrostQL.Core.Operations;

/// <summary>
/// Resolves a field of an object type an operation declares in
/// <see cref="IBifrostOperation.TypeDefinitions"/>: from the source's dictionary entry when the
/// operation returned a dictionary (the natural shape for data read through
/// <see cref="BifrostOperationContext.QueryAsync"/>), otherwise from its property or method, as
/// GraphQL.NET's default resolver does.
/// </summary>
internal sealed class OperationValueResolver : IFieldResolver
{
    public static readonly OperationValueResolver Instance = new();

    public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
    {
        var name = context.FieldDefinition.Name;
        return context.Source switch
        {
            IDictionary<string, object?> map => new(map.TryGetValue(name, out var value) ? value : null),
            IReadOnlyDictionary<string, object?> map => new(map.TryGetValue(name, out var value) ? value : null),
            _ => NameFieldResolver.Instance.ResolveAsync(context),
        };
    }

    /// <summary>The (type, field) pairs of every object type an operation declares.</summary>
    public static IEnumerable<(string TypeName, string FieldName)> DeclaredObjectFields(IBifrostOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.TypeDefinitions))
            yield break;

        var document = GraphQLParser.Parser.Parse(operation.TypeDefinitions);
        foreach (var type in document.Definitions.OfType<GraphQLObjectTypeDefinition>())
        {
            foreach (var field in type.Fields?.Items ?? new List<GraphQLFieldDefinition>())
                yield return (type.Name.StringValue, field.Name.StringValue);
        }
    }
}

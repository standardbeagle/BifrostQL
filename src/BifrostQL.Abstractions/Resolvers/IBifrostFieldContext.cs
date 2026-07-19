namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Bifrost-native field resolution context that replaces GraphQL.NET's IResolveFieldContext.
    /// Contains everything resolvers need without coupling to GraphQL.NET types.
    /// </summary>
    public interface IBifrostFieldContext
    {
        /// <summary>
        /// The GraphQL field name being resolved.
        /// Maps to IResolveFieldContext.FieldAst.Name.StringValue.
        /// </summary>
        string FieldName { get; }

        /// <summary>
        /// The field alias if specified in the query, null otherwise.
        /// Maps to IResolveFieldContext.FieldAst.Alias?.Name?.StringValue.
        /// </summary>
        string? FieldAlias { get; }

        /// <summary>
        /// The parent object for nested resolver dispatch.
        /// Maps to IResolveFieldContext.Source.
        /// </summary>
        object? Source { get; }

        /// <summary>
        /// The field path within the query, used for error reporting and observer context.
        /// Maps to IResolveFieldContext.Path.
        /// </summary>
        IReadOnlyList<object> Path { get; }

        /// <summary>
        /// User claims and authentication context.
        /// Maps to IResolveFieldContext.UserContext cast to IDictionary.
        /// </summary>
        IDictionary<string, object?> UserContext { get; }

        /// <summary>
        /// Dependency injection service provider from the request scope.
        /// Maps to IResolveFieldContext.RequestServices.
        /// </summary>
        IServiceProvider? RequestServices { get; }

        /// <summary>
        /// Whether the specified sub-field selections exist (non-null SubFields).
        /// Resolvers check this to verify the query has selected child fields.
        /// </summary>
        bool HasSubFields { get; }

        /// <summary>
        /// The parsed document AST, passed through to SqlVisitor for query building.
        /// Typed as object to avoid coupling to GraphQLParser.AST.GraphQLDocument.
        /// </summary>
        object Document { get; }

        /// <summary>
        /// Query variables, passed through to SqlContext for variable resolution.
        /// Typed as object to avoid coupling to GraphQL.Variables.
        /// </summary>
        object Variables { get; }

        /// <summary>
        /// Extension data attached to the request (connFactory, model, tableReaderFactory).
        /// Maps to IResolveFieldContext.InputExtensions.
        /// </summary>
        IDictionary<string, object?> InputExtensions { get; }

        /// <summary>
        /// Cancellation token for async operations.
        /// </summary>
        CancellationToken CancellationToken { get; }

        /// <summary>
        /// Returns true if the named argument was provided in the query.
        /// </summary>
        bool HasArgument(string name);

        /// <summary>
        /// Gets the typed value of a named argument.
        /// </summary>
        T? GetArgument<T>(string name);
    }
}

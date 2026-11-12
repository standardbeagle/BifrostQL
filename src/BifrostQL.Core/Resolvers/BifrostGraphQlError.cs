using GraphQL;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The one conversion from a <see cref="BifrostExecutionError"/> to the GraphQL error the
    /// client receives. The message is kept, and the error's <see cref="BifrostExecutionError.ErrorCode"/>
    /// (<c>ACCESS_DENIED</c>, <c>PENDING_APPROVAL</c>, <c>CONFLICT</c>, …) becomes
    /// <c>extensions.code</c>. Without it GraphQL.NET derives the code from the exception type,
    /// so every condition reached the wire as <c>BIFROST_EXECUTION_ERROR</c> and a client could
    /// not tell a denial from a conflict without matching message text. An error with no code
    /// keeps that derived code. A constraint violation's
    /// <see cref="BifrostExecutionError.ConstraintName"/> becomes <c>extensions.constraint</c>.
    /// </summary>
    internal static class BifrostGraphQlError
    {
        public static ExecutionError From(BifrostExecutionError error)
        {
            var graphQlError = new ExecutionError(error.Message, error);
            if (!string.IsNullOrWhiteSpace(error.ErrorCode))
                graphQlError.Code = error.ErrorCode;
            if (error.ConstraintName is { Length: > 0 } constraint)
                graphQlError.AddExtension("constraint", constraint);
            return graphQlError;
        }
    }
}

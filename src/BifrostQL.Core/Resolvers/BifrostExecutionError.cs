namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Bifrost-native execution error that replaces GraphQL.NET's ExecutionError.
    /// Represents an error encountered during field resolution or query execution.
    /// </summary>
    public class BifrostExecutionError : Exception
    {
        public BifrostExecutionError(string message)
            : base(message)
        {
        }

        public BifrostExecutionError(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// The field path where the error occurred, for structured error reporting.
        /// </summary>
        public IReadOnlyList<object>? ErrorPath { get; init; }

        /// <summary>
        /// Additional metadata about the error (error codes, hints, etc.).
        /// </summary>
        public IDictionary<string, object?>? Extensions { get; init; }
    }
}

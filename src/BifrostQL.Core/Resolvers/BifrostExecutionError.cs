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
        public IDictionary<string, object?>? Extensions { get; set; }
        
        /// <summary>
        /// Error code for programmatic handling.
        /// </summary>
        public string? ErrorCode { get; init; }
        
        /// <summary>
        /// Suggested actions the user can take to resolve this error.
        /// </summary>
        public IReadOnlyList<string>? SuggestedActions { get; init; }

        /// <summary>
        /// Creates a connection-related error with helpful suggestions.
        /// </summary>
        public static BifrostExecutionError ConnectionFailed(string details, Exception? inner = null)
        {
            var message = $"Database connection failed: {details}";
            var extensions = new Dictionary<string, object?>
            {
                ["errorCode"] = "CONNECTION_FAILED",
                ["suggestions"] = new[]
                {
                    "Verify your connection string is correct",
                    "Ensure the database server is running and accessible",
                    "Check firewall settings allow connections on the database port",
                    "Verify the database name exists"
                }
            };
            
            var ex = inner != null 
                ? new BifrostExecutionError(message, inner) { Extensions = extensions }
                : new BifrostExecutionError(message) { Extensions = extensions };
            
            return ex;
        }

        /// <summary>
        /// Creates a schema-related error.
        /// </summary>
        public static BifrostExecutionError SchemaError(string details, string? tableName = null)
        {
            return new BifrostExecutionError($"Schema error: {details}")
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["errorCode"] = "SCHEMA_ERROR",
                    ["tableName"] = tableName
                }
            };
        }

        /// <summary>
        /// Creates a query validation error.
        /// </summary>
        public static BifrostExecutionError QueryError(string details, string? queryPath = null)
        {
            return new BifrostExecutionError($"Query error: {details}")
            {
                ErrorPath = queryPath != null ? new[] { queryPath } : null,
                Extensions = new Dictionary<string, object?>
                {
                    ["errorCode"] = "QUERY_ERROR"
                }
            };
        }
    }
}

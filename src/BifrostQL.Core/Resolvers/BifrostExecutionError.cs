using System;

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

        /// <summary>Environment variable that opts into surfacing raw database
        /// error text to clients (local debugging only).</summary>
        public const string ExposeDbErrorsEnvVar = "BIFROST_EXPOSE_DB_ERRORS";

        /// <summary>Safe, client-facing message for a unique/duplicate-key
        /// violation. Carries no schema detail yet still conveys the actionable
        /// cause; downstream classification keys off <see cref="ErrorCode"/>.</summary>
        public const string ConflictMessage = "A conflicting record already exists.";

        /// <summary>
        /// Walks the exception chain for the fingerprints of a unique/duplicate-key
        /// violation. Keys off the words every supported driver puts in these
        /// messages — SQLite/SQL Server "UNIQUE constraint", MySQL "Duplicate
        /// entry", PostgreSQL "duplicate key ... unique constraint" — plus the
        /// distinctive Postgres SQLSTATE. Short numeric driver codes (1062, 2627,
        /// …) are deliberately NOT matched: those digits occur in ordinary error
        /// text (values, ids, offsets), so a substring test on them misclassifies
        /// unrelated failures as conflicts.
        /// </summary>
        public static bool IsUniqueViolation(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                var m = current.Message;
                if (m.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("23505", StringComparison.Ordinal))    // Postgres SQLSTATE unique_violation
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Wraps an exception raised while executing SQL for client return.
        /// Intentional <see cref="BifrostExecutionError"/>s (validation, tenant,
        /// policy) carry safe, authored messages and pass through unchanged.
        /// Raw driver/database exceptions can expose schema names, identifiers,
        /// and infrastructure detail, so their message is replaced with a generic
        /// one; the original exception is retained as <see cref="Exception.InnerException"/>
        /// for server-side logging. Set <see cref="ExposeDbErrorsEnvVar"/> to
        /// <c>1</c>/<c>true</c> to surface the raw text for local debugging.
        /// </summary>
        public static BifrostExecutionError FromDatabaseException(Exception ex)
        {
            if (ex is BifrostExecutionError bifrost)
                return bifrost;

            // A unique/duplicate-key violation is a client-actionable conflict, not
            // an infrastructure leak — surface a safe, stable message and code so
            // callers (e.g. workflow steps mapping to HTTP 409) can classify it
            // without string-matching raw driver text.
            if (IsUniqueViolation(ex))
                return new BifrostExecutionError(ConflictMessage, ex) { ErrorCode = "CONFLICT" };

            var expose = Environment.GetEnvironmentVariable(ExposeDbErrorsEnvVar);
            var reveal = string.Equals(expose, "1", StringComparison.Ordinal)
                || string.Equals(expose, "true", StringComparison.OrdinalIgnoreCase);

            var message = reveal
                ? $"Database error: {ex.Message}"
                : "A database error occurred while processing the request.";

            return new BifrostExecutionError(message, ex) { ErrorCode = "DATABASE_ERROR" };
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

using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Centralized error handling for BifrostQL that provides user-friendly error messages
    /// while preserving detailed diagnostics for logging.
    /// </summary>
    public interface IBifrostErrorHandler
    {
        /// <summary>
        /// Handles an exception and returns a user-friendly error message.
        /// </summary>
        BifrostErrorResult HandleError(Exception exception, ErrorContext context);
        
        /// <summary>
        /// Logs detailed error information for debugging.
        /// </summary>
        void LogError(Exception exception, ErrorContext context);
    }

    /// <summary>
    /// Result of error handling containing user-friendly message and error code.
    /// </summary>
    public sealed class BifrostErrorResult
    {
        public required string UserMessage { get; init; }
        public required string ErrorCode { get; init; }
        public ErrorSeverity Severity { get; init; } = ErrorSeverity.Error;
        public IReadOnlyDictionary<string, object?>? Extensions { get; init; }
        
        /// <summary>
        /// Suggested actions the user can take to resolve the error.
        /// </summary>
        public IReadOnlyList<string>? SuggestedActions { get; init; }
    }

    public enum ErrorSeverity
    {
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Context information about where an error occurred.
    /// </summary>
    public sealed class ErrorContext
    {
        public string? Operation { get; init; }
        public string? TableName { get; init; }
        public string? ColumnName { get; init; }
        public string? QueryPath { get; init; }
        public string? Sql { get; init; }
        public IDictionary<string, object?>? UserContext { get; init; }
        public string? CorrelationId { get; init; }
        
        /// <summary>
        /// Creates a context for database connection errors.
        /// </summary>
        public static ErrorContext Connection(string operation) =>
            new() { Operation = operation };
        
        /// <summary>
        /// Creates a context for query execution errors.
        /// </summary>
        public static ErrorContext Query(string tableName, string? queryPath = null, string? sql = null) =>
            new() { Operation = "Query", TableName = tableName, QueryPath = queryPath, Sql = sql };
        
        /// <summary>
        /// Creates a context for mutation errors.
        /// </summary>
        public static ErrorContext Mutation(string operation, string tableName) =>
            new() { Operation = operation, TableName = tableName };
        
        /// <summary>
        /// Creates a context for schema-related errors.
        /// </summary>
        public static ErrorContext Schema(string operation, string? tableName = null) =>
            new() { Operation = operation, TableName = tableName };
    }

    /// <summary>
    /// Default implementation of IBifrostErrorHandler that provides actionable error messages
    /// for common BifrostQL error scenarios.
    /// </summary>
    public sealed class BifrostErrorHandler : IBifrostErrorHandler
    {
        private readonly ILogger<BifrostErrorHandler>? _logger;

        public BifrostErrorHandler(ILogger<BifrostErrorHandler>? logger = null)
        {
            _logger = logger;
        }

        public BifrostErrorResult HandleError(Exception exception, ErrorContext context)
        {
            return exception switch
            {
                BifrostExecutionError bifrostError => HandleBifrostError(bifrostError, context),
                System.Data.Common.DbException dbEx => HandleDatabaseError(dbEx, context),
                InvalidOperationException invEx => HandleInvalidOperation(invEx, context),
                ArgumentException argEx => HandleArgumentError(argEx, context),
                _ => HandleGenericError(exception, context)
            };
        }

        public void LogError(Exception exception, ErrorContext context)
        {
            var correlationId = context.CorrelationId ?? Guid.NewGuid().ToString("N")[..8];
            
            _logger?.LogError(
                exception,
                "[{CorrelationId}] BifrostQL error in {Operation} for {Table}: {Message}",
                correlationId,
                context.Operation,
                context.TableName ?? "(unknown)",
                exception.Message
            );

            if (!string.IsNullOrEmpty(context.Sql) && _logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("[{CorrelationId}] SQL: {Sql}", correlationId, context.Sql);
            }
        }

        private BifrostErrorResult HandleBifrostError(BifrostExecutionError error, ErrorContext context)
        {
            var message = error.Message;
            var suggestions = new List<string>();
            
            // Check for specific error patterns and add suggestions
            if (message.Contains("connection", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Verify your connection string is correct");
                suggestions.Add("Ensure the database server is accessible");
                suggestions.Add("Check that the database exists");
            }
            
            if (message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Verify the user has appropriate database permissions");
                suggestions.Add("Check that the user can access the specified schema");
            }

            return new BifrostErrorResult
            {
                UserMessage = message,
                ErrorCode = "BIFROST_EXECUTION_ERROR",
                Severity = ErrorSeverity.Error,
                SuggestedActions = suggestions.Count > 0 ? suggestions : null,
                Extensions = error.Extensions as IReadOnlyDictionary<string, object?>
            };
        }

        private BifrostErrorResult HandleDatabaseError(System.Data.Common.DbException exception, ErrorContext context)
        {
            var errorCode = exception.GetType().Name;
            var message = exception.Message;
            var suggestions = new List<string>();
            string userMessage;
            string bifrostCode;

            // SQL Server specific error detection
            if (exception is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                switch (sqlEx.Number)
                {
                    case 18456: // Login failed
                        userMessage = "Database login failed. Please check your credentials.";
                        bifrostCode = "DB_LOGIN_FAILED";
                        suggestions.Add("Verify the username and password in your connection string");
                        suggestions.Add("Ensure the SQL Server authentication mode allows the login type");
                        break;
                    case 4060: // Cannot open database
                        userMessage = $"Cannot open database '{context.TableName ?? "(unknown)"}'. The database may not exist or you may not have access.";
                        bifrostCode = "DB_NOT_FOUND";
                        suggestions.Add("Verify the database name in your connection string");
                        suggestions.Add("Ensure the database has been created");
                        suggestions.Add("Check that the user has access to the database");
                        break;
                    case 53: // Network-related error
                    case 258: // Timeout
                        userMessage = "Cannot connect to the database server. Please verify the server is accessible.";
                        bifrostCode = "DB_CONNECTION_FAILED";
                        suggestions.Add("Verify the server name/IP address is correct");
                        suggestions.Add("Ensure the database server is running");
                        suggestions.Add("Check firewall settings allow connections");
                        suggestions.Add("Verify the port number if using a non-standard port");
                        break;
                    case 208: // Invalid object name
                        userMessage = $"Table or view not found: {context.TableName}. The schema may have changed.";
                        bifrostCode = "DB_OBJECT_NOT_FOUND";
                        suggestions.Add("Restart BifrostQL to refresh the schema cache");
                        suggestions.Add("Verify the table exists in the database");
                        break;
                    case 547: // Constraint violation
                        userMessage = "The operation violates a database constraint (foreign key, unique, or check constraint).";
                        bifrostCode = "DB_CONSTRAINT_VIOLATION";
                        suggestions.Add("Check that referenced records exist before creating relationships");
                        suggestions.Add("Ensure unique values for unique columns");
                        break;
                    default:
                        userMessage = $"Database error ({sqlEx.Number}): {message}";
                        bifrostCode = "DB_ERROR";
                        break;
                }
            }
            // PostgreSQL specific
            else if (exception.GetType().Name.Contains("Postgres", StringComparison.OrdinalIgnoreCase) ||
                     exception.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Contains("28P01", StringComparison.OrdinalIgnoreCase))
                {
                    userMessage = "Database login failed. Please check your credentials.";
                    bifrostCode = "DB_LOGIN_FAILED";
                    suggestions.Add("Verify the username and password in your connection string");
                }
                else if (message.Contains("3D000", StringComparison.OrdinalIgnoreCase))
                {
                    userMessage = "Database does not exist. Please verify the database name.";
                    bifrostCode = "DB_NOT_FOUND";
                    suggestions.Add("Verify the database name in your connection string");
                    suggestions.Add("Ensure the database has been created");
                }
                else
                {
                    userMessage = $"PostgreSQL error: {message}";
                    bifrostCode = "DB_ERROR";
                }
            }
            // MySQL specific
            else if (exception.GetType().Name.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Contains("1045", StringComparison.OrdinalIgnoreCase))
                {
                    userMessage = "Database login failed. Please check your credentials.";
                    bifrostCode = "DB_LOGIN_FAILED";
                    suggestions.Add("Verify the username and password");
                }
                else if (message.Contains("1049", StringComparison.OrdinalIgnoreCase))
                {
                    userMessage = "Unknown database. Please verify the database name.";
                    bifrostCode = "DB_NOT_FOUND";
                    suggestions.Add("Verify the database name in your connection string");
                }
                else
                {
                    userMessage = $"MySQL error: {message}";
                    bifrostCode = "DB_ERROR";
                }
            }
            else
            {
                userMessage = $"Database error: {message}";
                bifrostCode = "DB_ERROR";
            }

            return new BifrostErrorResult
            {
                UserMessage = userMessage,
                ErrorCode = bifrostCode,
                Severity = ErrorSeverity.Error,
                SuggestedActions = suggestions.Count > 0 ? suggestions : null
            };
        }

        private BifrostErrorResult HandleInvalidOperation(InvalidOperationException exception, ErrorContext context)
        {
            var message = exception.Message;
            var suggestions = new List<string>();

            if (message.Contains("schema", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Restart BifrostQL to reload the schema");
                suggestions.Add("Check that the database connection is valid");
            }

            if (message.Contains("connection", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Verify the connection string in configuration");
                suggestions.Add("Check that the database server is running");
            }

            return new BifrostErrorResult
            {
                UserMessage = message,
                ErrorCode = "INVALID_OPERATION",
                Severity = ErrorSeverity.Error,
                SuggestedActions = suggestions.Count > 0 ? suggestions : null
            };
        }

        private BifrostErrorResult HandleArgumentError(ArgumentException exception, ErrorContext context)
        {
            return new BifrostErrorResult
            {
                UserMessage = $"Invalid argument: {exception.Message}",
                ErrorCode = "INVALID_ARGUMENT",
                Severity = ErrorSeverity.Error,
                SuggestedActions = new[] { "Check the query syntax and parameter values" }
            };
        }

        private BifrostErrorResult HandleGenericError(Exception exception, ErrorContext context)
        {
            return new BifrostErrorResult
            {
                UserMessage = "An unexpected error occurred. Please check the logs for details.",
                ErrorCode = "INTERNAL_ERROR",
                Severity = ErrorSeverity.Error,
                SuggestedActions = new[] 
                { 
                    "Check the application logs for detailed error information",
                    "If the problem persists, restart the BifrostQL server"
                }
            };
        }
    }
}

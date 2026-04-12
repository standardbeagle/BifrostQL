using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Logging
{
    /// <summary>
    /// Extension methods for enhanced logging in BifrostQL.
    /// </summary>
    public static partial class BifrostLoggerExtensions
    {
        // High-performance logging with LoggerMessage for common operations
        
        [LoggerMessage(
            EventId = 1000,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] Schema loading started for connection: {ConnectionHash}")]
        public static partial void SchemaLoadingStarted(this ILogger logger, string correlationId, string connectionHash);

        [LoggerMessage(
            EventId = 1001,
            Level = LogLevel.Information,
            Message = "[{CorrelationId}] Schema loaded successfully: {TableCount} tables, {ColumnCount} columns in {DurationMs}ms")]
        public static partial void SchemaLoaded(this ILogger logger, string correlationId, int tableCount, int columnCount, long durationMs);

        [LoggerMessage(
            EventId = 1002,
            Level = LogLevel.Error,
            Message = "[{CorrelationId}] Schema loading failed: {ErrorMessage}")]
        public static partial void SchemaLoadingFailed(this ILogger logger, string correlationId, string errorMessage);

        [LoggerMessage(
            EventId = 2000,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] Query parsing started: {Operation}")]
        public static partial void QueryParsingStarted(this ILogger logger, string correlationId, string operation);

        [LoggerMessage(
            EventId = 2001,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] Query parsed: {TableCount} tables, {FieldCount} fields")]
        public static partial void QueryParsed(this ILogger logger, string correlationId, int tableCount, int fieldCount);

        [LoggerMessage(
            EventId = 2002,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] SQL generated for {TableName}: {SqlHash}")]
        public static partial void SqlGenerated(this ILogger logger, string correlationId, string tableName, string sqlHash);

        [LoggerMessage(
            EventId = 2003,
            Level = LogLevel.Information,
            Message = "[{CorrelationId}] Query executed: {TableName}, {RowCount} rows, {DurationMs}ms")]
        public static partial void QueryExecuted(this ILogger logger, string correlationId, string tableName, int rowCount, long durationMs);

        [LoggerMessage(
            EventId = 2004,
            Level = LogLevel.Warning,
            Message = "[{CorrelationId}] Slow query detected: {TableName}, {DurationMs}ms (threshold: {ThresholdMs}ms)")]
        public static partial void SlowQueryDetected(this ILogger logger, string correlationId, string tableName, long durationMs, int thresholdMs);

        [LoggerMessage(
            EventId = 3000,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] Mutation started: {Operation} on {TableName}")]
        public static partial void MutationStarted(this ILogger logger, string correlationId, string operation, string tableName);

        [LoggerMessage(
            EventId = 3001,
            Level = LogLevel.Information,
            Message = "[{CorrelationId}] Mutation completed: {Operation} on {TableName}, {RowCount} rows affected, {DurationMs}ms")]
        public static partial void MutationCompleted(this ILogger logger, string correlationId, string operation, string tableName, int rowCount, long durationMs);

        [LoggerMessage(
            EventId = 4000,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] Filter transformer applied: {TransformerType} on {TableName}")]
        public static partial void FilterTransformerApplied(this ILogger logger, string correlationId, string transformerType, string tableName);

        [LoggerMessage(
            EventId = 4001,
            Level = LogLevel.Debug,
            Message = "[{CorrelationId}] Module observer notified: {ObserverType} at phase {Phase}")]
        public static partial void ModuleObserverNotified(this ILogger logger, string correlationId, string observerType, string phase);

        /// <summary>
        /// Logs detailed SQL for debugging purposes (only at Debug level).
        /// </summary>
        public static void LogSqlDetail(this ILogger logger, string correlationId, string sql, IDictionary<string, object?>? parameters = null)
        {
            if (!logger.IsEnabled(LogLevel.Debug))
                return;

            logger.LogDebug("[{CorrelationId}] SQL Detail:\n{Sql}", correlationId, sql);
            
            if (parameters != null && parameters.Count > 0)
            {
                var paramStr = string.Join(", ", parameters.Select(p => $"{p.Key}={FormatParameterValue(p.Value)}"));
                logger.LogDebug("[{CorrelationId}] Parameters: {Parameters}", correlationId, paramStr);
            }
        }

        /// <summary>
        /// Logs schema cache operations.
        /// </summary>
        public static void LogCacheOperation(this ILogger logger, string operation, string key, bool hit)
        {
            if (!logger.IsEnabled(LogLevel.Debug))
                return;

            logger.LogDebug(
                "Schema cache {Operation}: key={Key}, result={Result}",
                operation,
                key,
                hit ? "HIT" : "MISS");
        }

        /// <summary>
        /// Logs configuration loading details.
        /// </summary>
        public static void LogConfigurationLoaded(this ILogger logger, string source, int metadataRuleCount)
        {
            logger.LogInformation(
                "Configuration loaded from {Source} with {RuleCount} metadata rules",
                source,
                metadataRuleCount);
        }

        private static string FormatParameterValue(object? value)
        {
            if (value == null)
                return "NULL";
            if (value is string s && s.Length > 50)
                return $"'{s[..50]}...' (truncated)";
            if (value is string str)
                return $"'{str}'";
            return value.ToString() ?? "(null)";
        }
    }

    /// <summary>
    /// Helper class for generating correlation IDs.
    /// </summary>
    public static class CorrelationIdGenerator
    {
        /// <summary>
        /// Generates a short, unique correlation ID for tracing operations.
        /// </summary>
        public static string Generate()
        {
            return Guid.NewGuid().ToString("N")[..8];
        }
    }
}

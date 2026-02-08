using BifrostQL.Core.Modules;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Logging;

public sealed class QueryLoggingObserver : IQueryObserver
{
    private readonly ILogger<QueryLoggingObserver> _logger;
    private readonly BifrostLoggingConfiguration _config;

    public QueryPhase[] Phases { get; } = [QueryPhase.Parsed, QueryPhase.Transformed, QueryPhase.BeforeExecute, QueryPhase.AfterExecute];

    public QueryLoggingObserver(ILogger<QueryLoggingObserver> logger, BifrostLoggingConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
    {
        var correlationId = context.UserContext.TryGetValue("_correlationId", out var cid) ? cid as string : null;

        switch (phase)
        {
            case QueryPhase.Parsed:
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Query parsed: Table={Table} Type={QueryType} Path={Path} CorrelationId={CorrelationId}",
                        context.Table.DbName, context.QueryType, context.Path, correlationId);
                break;

            case QueryPhase.Transformed:
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Query transformed: Table={Table} FilterApplied={FilterApplied} CorrelationId={CorrelationId}",
                        context.Table.DbName, context.Filter != null, correlationId);
                break;

            case QueryPhase.BeforeExecute:
                if (_config.LogSql && _logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Query executing: Table={Table} Sql={Sql} CorrelationId={CorrelationId}",
                        context.Table.DbName, context.Sql, correlationId);
                break;

            case QueryPhase.AfterExecute:
                var durationMs = context.Duration?.TotalMilliseconds ?? 0;
                if (durationMs >= _config.SlowQueryThresholdMs)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Slow query: Table={Table} Duration={Duration}ms RowCount={RowCount} Sql={Sql} CorrelationId={CorrelationId}",
                            context.Table.DbName, durationMs, context.RowCount, context.Sql, correlationId);
                }
                else if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Query completed: Table={Table} Duration={Duration}ms RowCount={RowCount} CorrelationId={CorrelationId}",
                        context.Table.DbName, durationMs, context.RowCount, correlationId);
                }
                break;
        }

        return ValueTask.CompletedTask;
    }
}

using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Lifecycle phases where observers can be notified.
/// </summary>
public enum QueryPhase
{
    /// <summary>Query parsed from GraphQL, GqlObjectQuery created</summary>
    Parsed,

    /// <summary>After all filter transformers applied</summary>
    Transformed,

    /// <summary>SQL built, about to execute</summary>
    BeforeExecute,

    /// <summary>SQL execution complete, results available</summary>
    AfterExecute,
}

/// <summary>
/// Observes query lifecycle events for side effects (auditing, metrics, caching).
/// Observers should NOT modify the query - use IFilterTransformer for that.
/// Observer exceptions are logged but do not abort the query.
/// </summary>
public interface IQueryObserver
{
    /// <summary>
    /// Which phases this observer wants to be notified for.
    /// </summary>
    QueryPhase[] Phases { get; }

    /// <summary>
    /// Called when a query reaches one of the observer's phases.
    /// This method should be fast and non-blocking.
    /// For expensive operations, queue work to a background service.
    /// </summary>
    ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context);
}

/// <summary>
/// Context provided to query observers.
/// </summary>
public sealed class QueryObserverContext
{
    public required IDbTable Table { get; init; }
    public required IDbModel Model { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
    public required QueryType QueryType { get; init; }
    public required string Path { get; init; }

    /// <summary>
    /// The filter being applied (after transformation). Available in Transformed+ phases.
    /// </summary>
    public TableFilter? Filter { get; init; }

    /// <summary>
    /// The generated SQL. Available in BeforeExecute+ phases.
    /// </summary>
    public string? Sql { get; init; }

    /// <summary>
    /// Number of rows returned. Available in AfterExecute phase only.
    /// </summary>
    public int? RowCount { get; init; }

    /// <summary>
    /// Execution duration. Available in AfterExecute phase only.
    /// </summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Composite wrapper for multiple query observers.
/// </summary>
public interface IQueryObservers : IReadOnlyCollection<IQueryObserver>
{
    ValueTask NotifyAsync(QueryPhase phase, QueryObserverContext context);
}

public sealed class QueryObserversWrap : IQueryObservers
{
    public IReadOnlyCollection<IQueryObserver> Observers { get; init; } = Array.Empty<IQueryObserver>();
    public Action<Exception, IQueryObserver, QueryPhase>? OnError { get; init; }

    public int Count => Observers.Count;

    public async ValueTask NotifyAsync(QueryPhase phase, QueryObserverContext context)
    {
        foreach (var observer in Observers.Where(o => o.Phases.Contains(phase)))
        {
            try
            {
                await observer.OnQueryPhaseAsync(phase, context);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex, observer, phase);
            }
        }
    }

    public IEnumerator<IQueryObserver> GetEnumerator() => Observers.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

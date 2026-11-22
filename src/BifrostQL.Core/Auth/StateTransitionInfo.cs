using BifrostQL.Core.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Core.Auth;

/// <summary>
/// A committed state-machine transition. <see cref="Table"/> is the resolved table (one name can
/// exist in two schemas) and <see cref="EntityKey"/> its full primary key, column name to value,
/// so a composite-key row is identified. <see cref="Model"/> is the model the write ran against,
/// for observers that resolve configured table names.
/// </summary>
public sealed record StateTransitionInfo(
    IDbModel Model,
    IDbTable Table,
    IReadOnlyDictionary<string, object?> EntityKey,
    string From,
    string To,
    string? Actor,
    string? EventName)
{
    /// <summary>The schema-qualified table name, <c>schema.name</c>.</summary>
    public string Entity => $"{Table.TableSchema}.{Table.DbName}";
}

public interface IStateTransitionObserver
{
    ValueTask OnTransitionAsync(StateTransitionInfo transition, IDictionary<string, object?> userContext);
}

public sealed class StateTransitionObservers
{
    private readonly IReadOnlyCollection<IStateTransitionObserver> _observers;
    private readonly ILogger<StateTransitionObservers> _logger;

    public StateTransitionObservers(
        IReadOnlyCollection<IStateTransitionObserver> observers,
        ILogger<StateTransitionObservers>? logger = null)
    {
        _observers = observers;
        _logger = logger ?? NullLogger<StateTransitionObservers>.Instance;
    }

    // Observers fire after the state-changing SQL has committed, so a throw
    // here cannot undo the transition — surfacing it would just mask the
    // committed change. Log per-observer failures and continue.
    public async ValueTask NotifyAsync(StateTransitionInfo transition, IDictionary<string, object?> userContext)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnTransitionAsync(transition, userContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "State-transition observer {Observer} threw on {Entity} {From}->{To}.",
                    observer.GetType().FullName,
                    transition.Entity,
                    transition.From,
                    transition.To);
            }
        }
    }
}

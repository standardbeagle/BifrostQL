using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Core.Auth;

public sealed record StateTransitionInfo(
    string Entity,
    object? EntityId,
    string From,
    string To,
    string? Actor,
    string? EventName);

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

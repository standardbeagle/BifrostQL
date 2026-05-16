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

    public StateTransitionObservers(IReadOnlyCollection<IStateTransitionObserver> observers)
    {
        _observers = observers;
    }

    public async ValueTask NotifyAsync(StateTransitionInfo transition, IDictionary<string, object?> userContext)
    {
        foreach (var observer in _observers)
            await observer.OnTransitionAsync(transition, userContext);
    }
}

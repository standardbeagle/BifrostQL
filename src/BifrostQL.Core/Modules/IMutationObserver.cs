using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

public interface IMutationObserver
{
    ValueTask OnMutationAsync(MutationObserverContext context);
}

public sealed class MutationObserverContext
{
    public required IDbTable Table { get; init; }
    public required MutationType MutationType { get; init; }
    public required IDictionary<string, object?> Data { get; init; }
    public required object? Result { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
}

public sealed class MutationObservers
{
    private readonly IReadOnlyCollection<IMutationObserver> _observers;

    public MutationObservers(IReadOnlyCollection<IMutationObserver> observers)
    {
        _observers = observers;
    }

    public async ValueTask NotifyAsync(MutationObserverContext context)
    {
        foreach (var observer in _observers)
            await observer.OnMutationAsync(context);
    }
}

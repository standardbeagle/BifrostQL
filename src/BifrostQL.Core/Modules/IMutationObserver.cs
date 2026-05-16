using BifrostQL.Core.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<MutationObservers> _logger;

    public MutationObservers(
        IReadOnlyCollection<IMutationObserver> observers,
        ILogger<MutationObservers>? logger = null)
    {
        _observers = observers;
        _logger = logger ?? NullLogger<MutationObservers>.Instance;
    }

    // Observers must not abort the caller's mutation — the SQL may already be
    // committed by the time we notify. Per-observer failures are logged and
    // swallowed so one bad observer can't poison the rest.
    public async ValueTask NotifyAsync(MutationObserverContext context)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnMutationAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Mutation observer {Observer} threw on {MutationType} of {Table}.",
                    observer.GetType().FullName,
                    context.MutationType,
                    context.Table.DbName);
            }
        }
    }
}

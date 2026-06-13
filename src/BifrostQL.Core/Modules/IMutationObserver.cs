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

// A before-commit veto hook. Unlike IMutationObserver (which fires AFTER the
// write and cannot abort), this runs immediately BEFORE the single-statement
// write in DbTableMutateResolver. Returning a non-empty error list — or
// throwing — aborts the mutation so the write never happens. Use for
// outbox/domain-event/veto patterns where post-commit metrics are insufficient.
//
// NOTE on the context: BeforeCommitAsync reuses MutationObserverContext for a
// uniform shape, but the write has NOT happened yet, so context.Result is
// always null in this phase. Do not depend on Result here.
public interface IBeforeCommitMutationHook
{
    ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context);
}

// Composite for before-commit hooks. Parallel to MutationObservers, but with the
// OPPOSITE failure contract: it does NOT swallow. Hooks run in registration
// order; their returned errors are aggregated, and a thrown exception
// propagates. The resolver turns a non-empty aggregate (or a propagated throw)
// into an aborted mutation before any SQL executes.
public sealed class BeforeCommitMutationHooks
{
    private readonly IReadOnlyCollection<IBeforeCommitMutationHook> _hooks;

    public BeforeCommitMutationHooks(IReadOnlyCollection<IBeforeCommitMutationHook> hooks)
    {
        _hooks = hooks;
    }

    // Runs every hook in order and aggregates returned errors. Exceptions are
    // NOT caught — a throwing hook aborts the mutation just as returned errors
    // do. Returns an empty list when all hooks pass (the write may proceed).
    public async ValueTask<IReadOnlyList<string>> RunAsync(MutationObserverContext context)
    {
        List<string>? errors = null;
        foreach (var hook in _hooks)
        {
            var hookErrors = await hook.BeforeCommitAsync(context);
            if (hookErrors is { Count: > 0 })
                (errors ??= new List<string>()).AddRange(hookErrors);
        }
        return (IReadOnlyList<string>?)errors ?? Array.Empty<string>();
    }
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

using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
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

    // --- Before-commit-only ambient state ---
    // These are populated ONLY during the before-commit phase (by
    // MutationNotifier.RunBeforeCommitHooksAsync), where a hook may need to write
    // into the SAME transaction as the data change — the transactional-outbox /
    // domain-event pattern. In the post-commit observer phase the transaction has
    // already committed and these are all null, exactly like Result is null in the
    // before-commit phase. A before-commit hook that writes SQL must execute it on
    // Connection/Transaction so its write commits or rolls back atomically with the
    // mutation.

    /// <summary>The open connection for the in-flight mutation (before-commit only; else null).</summary>
    public DbConnection? Connection { get; init; }

    /// <summary>The open transaction wrapping the mutation (before-commit only; else null).</summary>
    public DbTransaction? Transaction { get; init; }

    /// <summary>The database model, for reading model-level metadata (before-commit only; else null).</summary>
    public IDbModel? Model { get; init; }

    /// <summary>The SQL dialect for the in-flight mutation's connection (before-commit only; else null).</summary>
    public ISqlDialect? Dialect { get; init; }
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

// An after-write, in-transaction hook. Unlike IBeforeCommitMutationHook (which
// runs BEFORE the write and cannot see its result) this runs immediately AFTER
// the write but still INSIDE the same transaction, so context.Result carries the
// write outcome — crucially, the database-generated identity on an INSERT. This
// is the correct seam for the transactional outbox / domain-event pattern: the
// event row is written in the same transaction as the data change AND can name
// the generated key. A thrown exception is NOT swallowed — it rolls the whole
// mutation back, so a failure to record the event prevents the data commit.
public interface IInTransactionMutationHook
{
    ValueTask AfterWriteInTransactionAsync(MutationObserverContext context);
}

// Composite for after-write in-transaction hooks. Runs each in registration order
// and does NOT swallow: a throw propagates so the caller's transaction rolls back
// (exactly-once — no event without its data change, no data change without its event).
public sealed class InTransactionMutationHooks
{
    private readonly IReadOnlyCollection<IInTransactionMutationHook> _hooks;

    public InTransactionMutationHooks(IReadOnlyCollection<IInTransactionMutationHook> hooks)
    {
        _hooks = hooks;
    }

    public async ValueTask RunAsync(MutationObserverContext context)
    {
        foreach (var hook in _hooks)
            await hook.AfterWriteInTransactionAsync(context);
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

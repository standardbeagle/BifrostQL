using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>One written row of a set write: its verb, its data as written, its key and its own state bag.</summary>
/// <param name="MutationType">The row's own verb: one write (a batch, a delta) can mix them.</param>
/// <param name="Data">The row's data after the transformer chain, keyed by database column name.</param>
/// <param name="Key">The row's key: the generated identity on an insert, the key value(s) otherwise.</param>
/// <param name="MutationState">The bag this row's transformer chain and before-commit hooks wrote into.</param>
public sealed record MutationSetRow(
    MutationType MutationType,
    IDictionary<string, object?> Data,
    object? Key,
    IDictionary<string, object?> MutationState);

/// <summary>
/// One write of many rows (or one), handed to an <see cref="IInTransactionSetMutationHook"/> after
/// the write and before commit, on the write's own connection and transaction.
/// </summary>
public sealed record MutationSetContext
{
    public required IDbTable Table { get; init; }

    /// <summary>Every row the write changed, in the order the caller gave them. Never empty.</summary>
    public required IReadOnlyList<MutationSetRow> Rows { get; init; }

    public required IDictionary<string, object?> UserContext { get; init; }
    public required DbConnection Connection { get; init; }

    /// <summary>The write's transaction; null on a path that drives it with SQL BEGIN/COMMIT.</summary>
    public DbTransaction? Transaction { get; init; }

    public IDbModel? Model { get; init; }
    public ISqlDialect? Dialect { get; init; }
    public IDbConnFactory? ConnFactory { get; init; }

    /// <summary>The bag every action of the write's transaction shares (<see cref="MutationObserverContext.TransactionState"/>).</summary>
    public required IDictionary<string, object?> TransactionState { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>The one-row set a single-row write hands a set hook.</summary>
    internal static MutationSetContext FromRow(MutationObserverContext context) => new()
    {
        Table = context.Table,
        Rows = new[] { new MutationSetRow(context.MutationType, context.Data, context.Result, context.MutationState) },
        UserContext = context.UserContext,
        Connection = context.Connection
            ?? throw new InvalidOperationException("An in-transaction hook ran without the write's connection."),
        Transaction = context.Transaction,
        Model = context.Model,
        Dialect = context.Dialect,
        ConnFactory = context.ConnFactory,
        TransactionState = context.TransactionState,
        CancellationToken = context.CancellationToken,
    };
}

/// <summary>
/// An after-write, in-transaction hook that takes a whole write at once: it is called ONCE per
/// write with every written row's data, key and <see cref="MutationObserverContext.MutationState"/>
/// in row order, instead of once per row, so a hook that writes a sibling row per written row can
/// do it in one statement. A single-row write calls it with one row. A write that changed no row
/// does not call it. A throw rolls the write back, as for <see cref="IInTransactionMutationHook"/>.
///
/// <para>Register it as an <see cref="IInTransactionMutationHook"/>: the composite recognises the
/// set form and never also calls it per row. A hook that implements only the per-row interface
/// keeps its per-row calls on every path.</para>
/// </summary>
public interface IInTransactionSetMutationHook : IInTransactionMutationHook
{
    ValueTask AfterSetWriteInTransactionAsync(MutationSetContext context);

    /// <summary>A single-row write reaches a set hook as a one-row set; nothing written, no call.</summary>
    ValueTask IInTransactionMutationHook.AfterWriteInTransactionAsync(MutationObserverContext context)
        => context.AffectedRows is null or > 0
            ? AfterSetWriteInTransactionAsync(MutationSetContext.FromRow(context))
            : ValueTask.CompletedTask;
}

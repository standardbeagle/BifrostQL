using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Auth;

namespace BifrostQL.Core.Modules;

/// <summary>
/// The type of mutation operation.
/// </summary>
public enum MutationType
{
    Insert,
    Update,
    Delete
}

/// <summary>
/// Result of a mutation transformation.
/// </summary>
public sealed class MutationTransformResult
{
    /// <summary>
    /// The transformed mutation type. May differ from original (e.g., DELETE → UPDATE for soft-delete).
    /// </summary>
    public required MutationType MutationType { get; init; }

    /// <summary>
    /// The transformed data dictionary.
    /// </summary>
    public required Dictionary<string, object?> Data { get; init; }

    /// <summary>
    /// Error messages. If any, mutation is aborted.
    /// </summary>
    public string[] Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Classification for <see cref="Errors"/>, propagated onto the
    /// <see cref="BifrostQL.Core.Resolvers.BifrostExecutionError"/> the pipeline
    /// throws so every transport gate maps the abort by its CONDITION, not by op
    /// class. An access-denial rejection (policy action/column write-deny) sets
    /// <see cref="BifrostQL.Core.Resolvers.BifrostExecutionError.AccessDeniedCode"/>
    /// so a policy-denied WRITE surfaces the SAME status as the read-side deny
    /// (the single-funnel-needs-condition-tagging lesson). Validation, enum,
    /// concurrency and state-machine rejections leave it null so they stay a
    /// generic fault — an access-denied code must never be blanket-stamped over a
    /// non-authorization error (that would mask a genuine INTERNAL).
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Additional filter to apply (e.g., for soft-delete to add deleted_at IS NULL to UPDATE/DELETE).
    /// </summary>
    public TableFilter? AdditionalFilter { get; init; }

    public StateTransitionInfo? StateTransition { get; init; }

    /// <summary>
    /// When true, the write affecting zero rows is a concurrency CONFLICT, not a
    /// silent no-op. The optimistic-concurrency transformer sets this after ANDing a
    /// version-token predicate into the UPDATE WHERE: a zero-row result then means the
    /// stored token no longer matches (a lost update), so the executor raises a
    /// CONFLICT error. Left false for tenant/policy/soft-delete out-of-scope updates,
    /// which legitimately affect zero rows and must stay silent.
    /// </summary>
    public bool ConflictOnNoRows { get; init; }

    /// <summary>
    /// Aborts the mutation when the transformer chain reported <see cref="Errors"/>,
    /// throwing a <see cref="BifrostQL.Core.Resolvers.BifrostExecutionError"/> that
    /// carries <see cref="ErrorCode"/>. No-op when there are no errors.
    ///
    /// EVERY mutation execution path (single-row pipeline, batch pipeline, tree-sync,
    /// the file resolvers) MUST funnel its "non-empty Errors → throw" step through this
    /// one method rather than hand-rolling the throw. The ErrorCode is the CONDITION
    /// signal every transport funnel maps on (policy/tenant denial → the denied wire
    /// status); a hand-rolled `throw new BifrostExecutionError(string.Join(...))` that
    /// forgets `{ ErrorCode = ... }` silently downgrades a denial to a generic INTERNAL
    /// on one op class — exactly the cross-op-class divergence
    /// .claude/rules/protocol-adapter-security.md rule 10 exists to prevent, and a bug
    /// that recurred independently across the batch and file-resolver paths. Centralising
    /// it here makes the code impossible to forget.
    /// </summary>
    public void ThrowIfDenied()
    {
        if (Errors.Length == 0)
            return;
        throw new Resolvers.BifrostExecutionError(string.Join("; ", Errors)) { ErrorCode = ErrorCode };
    }
}

/// <summary>
/// Transforms mutations before execution. Can change the mutation type itself,
/// rewrite the data, add filters, or reject with errors.
/// Example: Convert DELETE to UPDATE for soft-delete.
/// </summary>
public interface IMutationTransformer
{
    /// <summary>
    /// Priority for transformer ordering. Lower = applied first. Bands (see
    /// Modules/README.md): 0-99 security, 100-199 data filtering, 200+ app.
    ///
    /// ORDERING INVARIANT: <see cref="MutationTransformersWrap.TransformAsync"/>
    /// re-evaluates <see cref="AppliesTo"/> against the CURRENT mutation type each
    /// iteration. <c>SoftDeleteMutationTransformer</c> (priority 100) rewrites
    /// DELETE→UPDATE, so any transformer with priority &gt; 100 that gates on
    /// <see cref="MutationType.Delete"/> will never fire on a soft-deleted row.
    /// A transformer that must observe deletes MUST sit below priority 100 (this is
    /// why <c>AuditMutationTransformer</c> is at 50). Covered by
    /// MutationTransformerCompositionTests.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Determines if this transformer applies to the given table and mutation type.
    /// </summary>
    bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context);

    /// <summary>
    /// Transforms the mutation. Can change type, data, or add filters.
    /// </summary>
    ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context);

    /// <summary>
    /// True when this transformer must read through the write's own connection and
    /// transaction (<see cref="MutationTransformContext.Connection"/>) on
    /// <paramref name="table"/>. The bulk set-based batch fast path transforms its rows
    /// before any write transaction exists, so a table for which any transformer answers
    /// true is kept on the per-row batch path, where the context carries the transaction.
    /// Must use the same predicate the transformer's own body uses to decide whether it
    /// reads (the hook-applicability rule in AGENTS.md). Defaults to false: a transformer
    /// that never touches <see cref="MutationTransformContext.Connection"/> needs nothing.
    /// </summary>
    bool RequiresWriteTransaction(IDbTable table) => false;
}

/// <summary>
/// Context for mutation transformations.
/// </summary>
public sealed class MutationTransformContext
{
    public required IDbModel Model { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
    public IReadOnlyDictionary<string, object?>? CurrentRow { get; init; }
    public IServiceProvider? Services { get; init; }

    /// <summary>
    /// The connection the write itself runs on. Every transformer chain run on a write
    /// path — single-row insert/update/delete/upsert, the per-row batch path, the
    /// filtered set-update, tree-sync, and the row-scope probe that precedes a state
    /// load — happens INSIDE the write's transaction and receives it here, so a guard
    /// can read (and lock) state that commits or rolls back with the write instead of
    /// opening a second connection that can neither see uncommitted rows of the same
    /// mutation nor hold a lock across it. Null only where no write transaction exists
    /// yet: the bulk set-based batch fast path builds its plan before its transaction
    /// opens (a transformer that needs the connection declares
    /// <see cref="IMutationTransformer.RequiresWriteTransaction"/> and the table stays
    /// on the per-row path), and a context a caller constructs by hand.
    /// </summary>
    public DbConnection? Connection { get; init; }

    /// <summary>
    /// The ADO.NET transaction of the write, set on every command a transformer issues on
    /// <see cref="Connection"/>. Null on tree-sync even though <see cref="Connection"/> is
    /// set: that path manages its transaction with SQL <c>BEGIN</c>/<c>COMMIT</c> on the
    /// connection, so commands run on the connection's ambient transaction and there is
    /// no <see cref="DbTransaction"/> object to pass.
    /// </summary>
    public DbTransaction? Transaction { get; init; }

    /// <summary>The write's SQL dialect; set whenever <see cref="Connection"/> is.</summary>
    public ISqlDialect? Dialect { get; init; }

    /// <summary>The write's cancellation token, for commands a transformer issues.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Reads the named <paramref name="columns"/> of the row of <paramref name="table"/>
    /// matching <paramref name="keyColumns"/> (an AND of column equalities, database
    /// column names) inside the write's transaction, taking the dialect's update lock
    /// (<see cref="ISqlDialect.UpdateLockTableHint"/> / <see cref="ISqlDialect.UpdateLockClause"/>;
    /// SQLite serializes writers and emits neither) so the row cannot change before the
    /// write commits. Returns null when no row matches. The predicate is exactly the
    /// supplied columns — it is NOT narrowed by tenant/policy scope, so a guard that
    /// answers the caller from the result must apply its own scope (as the tenant
    /// foreign-key check compares the tenant column) and must answer a missing row and
    /// an out-of-scope row identically. Throws when the context carries no write
    /// transaction (see <see cref="Connection"/>), never reads outside it.
    /// </summary>
    public Task<IReadOnlyDictionary<string, object?>?> LoadRowForUpdateAsync(
        IDbTable table,
        IReadOnlyCollection<string> columns,
        IReadOnlyDictionary<string, object?> keyColumns)
    {
        if (Connection is null || Dialect is null)
            throw new Resolvers.BifrostExecutionError(
                "This mutation path does not run its transformers inside the write transaction.");
        return Resolvers.MutationCommandExecutor.LoadRowByKey(
            Connection, Transaction, Dialect, table, columns,
            new Dictionary<string, object?>(keyColumns, StringComparer.OrdinalIgnoreCase),
            forUpdate: true, CancellationToken);
    }

    /// <summary>
    /// Copies this context, replacing only what is supplied. Every pipeline that derives
    /// a per-action context (current row, module arguments, the write transaction) goes
    /// through here so no derived context silently drops a member the base carried.
    /// </summary>
    internal MutationTransformContext With(
        IReadOnlyDictionary<string, object?>? currentRow = null,
        IReadOnlyDictionary<string, object?>? moduleArguments = null,
        (DbConnection Connection, DbTransaction? Transaction, ISqlDialect Dialect, CancellationToken CancellationToken)? write = null)
        => new()
        {
            Model = Model,
            UserContext = UserContext,
            CurrentRow = currentRow ?? CurrentRow,
            Services = Services,
            ModuleArguments = moduleArguments ?? ModuleArguments,
            RestoreSoftDeleted = RestoreSoftDeleted,
            RestoreHardDeleted = RestoreHardDeleted,
            Connection = write?.Connection ?? Connection,
            Transaction = write is { } w ? w.Transaction : Transaction,
            Dialect = write?.Dialect ?? Dialect,
            CancellationToken = write?.CancellationToken ?? CancellationToken,
        };

    /// <summary>
    /// Module argument values captured from the GraphQL request (e.g.
    /// <c>_hardDelete</c>), keyed by the module's context key. See
    /// <see cref="ModuleApiRegistry.CaptureMutationArguments"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ModuleArguments { get; init; } = ModuleApiRegistry.EmptyArguments;

    /// <summary>Set only by the deferred undo pipeline path to restore a soft-deleted row.</summary>
    public bool RestoreSoftDeleted { get; init; }

    /// <summary>Set only by the deferred undo pipeline path to reinsert a hard-deleted row.</summary>
    public bool RestoreHardDeleted { get; init; }
}

/// <summary>
/// Composite wrapper for multiple mutation transformers.
///
/// <para>The wrap owns the mutation payload's NAME SPACE: it rekeys the incoming
/// data from GraphQL field names to database column names ONCE, before the first
/// transformer runs, and every transformer therefore sees database column names
/// only (see <see cref="MutationTransformersWrap.TransformAsync"/>). Callers must
/// route transformer application through this seam rather than invoking
/// transformers directly, or a renamed column reaches them under a name none of
/// their metadata (write-deny lists, state column, audit columns, enum column map)
/// is expressed in.</para>
/// </summary>
public interface IMutationTransformers : IReadOnlyCollection<IMutationTransformer>
{
    ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context);
}

public sealed class MutationTransformersWrap : IMutationTransformers
{
    public IReadOnlyCollection<IMutationTransformer> Transformers { get; init; } = Array.Empty<IMutationTransformer>();

    public int Count => Transformers.Count;

    public async ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var currentType = mutationType;
        // Rekey GraphQL field names to real database column names ONCE, before any
        // transformer runs. Client input is keyed by GraphQL field name, which can be
        // sanitized or prefixed and so differ from the column ("sale-price" surfaces
        // as "sale_price"); transformer configuration — policy write-deny lists, the
        // state-machine state column, audit populate columns, the enum column map — is
        // expressed in database column names. Splitting the two name spaces let a
        // renamed column slip past the write-deny check and skip state-transition
        // validation entirely. One rekey here means every transformer, and the SQL
        // built from its output, shares one name space; keys that are already column
        // names (tenant, soft-delete, audit) pass through untouched.
        var currentData = Resolvers.DbParameterBinder.ToDbColumnKeys(table, data);
        var allErrors = new List<string>();
        // The classification of the FIRST transformer to abort (transformers run in
        // priority order, so the security band — policy/tenant — is seen first).
        // Captured once and never overwritten so a later codeless validation error
        // cannot recolor an earlier denial, and a codeless first-abort is never
        // upgraded to a denial. Only an access-denial condition carries a code; a
        // validation/enum/concurrency error leaves it null → generic fault.
        string? errorCode = null;
        var errorCodeCaptured = false;
        TableFilter? combinedFilter = null;
        StateTransitionInfo? stateTransition = null;
        var conflictOnNoRows = false;

        foreach (var transformer in Transformers.OrderBy(t => t.Priority))
        {
            if (!transformer.AppliesTo(table, currentType, context))
                continue;

            var result = await transformer.TransformAsync(table, currentType, currentData, context);

            if (result.Errors.Length > 0)
            {
                allErrors.AddRange(result.Errors);
                if (!errorCodeCaptured)
                {
                    errorCode = result.ErrorCode;
                    errorCodeCaptured = true;
                }
            }

            currentType = result.MutationType;
            currentData = result.Data;

            if (result.AdditionalFilter != null)
            {
                combinedFilter = combinedFilter == null
                    ? result.AdditionalFilter
                    : CombineFilters(combinedFilter, result.AdditionalFilter);
            }

            stateTransition ??= result.StateTransition;
            conflictOnNoRows |= result.ConflictOnNoRows;
        }

        return new MutationTransformResult
        {
            MutationType = currentType,
            Data = currentData,
            Errors = allErrors.ToArray(),
            ErrorCode = errorCode,
            AdditionalFilter = combinedFilter,
            StateTransition = stateTransition,
            ConflictOnNoRows = conflictOnNoRows,
        };
    }

    private static TableFilter CombineFilters(TableFilter existing, TableFilter additional) =>
        TableFilter.CombineAnd(existing, additional);

    public IEnumerator<IMutationTransformer> GetEnumerator() => Transformers.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

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
        var currentData = data;
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

    private static TableFilter CombineFilters(TableFilter existing, TableFilter additional)
    {
        return new TableFilter
        {
            And = new List<TableFilter> { existing, additional },
            FilterType = QueryModel.FilterType.And,
        };
    }

    public IEnumerator<IMutationTransformer> GetEnumerator() => Transformers.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

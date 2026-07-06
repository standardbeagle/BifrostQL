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
    /// Additional filter to apply (e.g., for soft-delete to add deleted_at IS NULL to UPDATE/DELETE).
    /// </summary>
    public TableFilter? AdditionalFilter { get; init; }

    public StateTransitionInfo? StateTransition { get; init; }
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
        TableFilter? combinedFilter = null;
        StateTransitionInfo? stateTransition = null;

        foreach (var transformer in Transformers.OrderBy(t => t.Priority))
        {
            if (!transformer.AppliesTo(table, currentType, context))
                continue;

            var result = await transformer.TransformAsync(table, currentType, currentData, context);

            if (result.Errors.Length > 0)
            {
                allErrors.AddRange(result.Errors);
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
        }

        return new MutationTransformResult
        {
            MutationType = currentType,
            Data = currentData,
            Errors = allErrors.ToArray(),
            AdditionalFilter = combinedFilter,
            StateTransition = stateTransition,
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

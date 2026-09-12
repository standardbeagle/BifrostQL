using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Resolvers;

/// <summary>
/// Carries the per-request column mask set from the transformer pass (which
/// computes it, per table occurrence, from the registered
/// <c>IColumnReadGuard.MaskedColumns</c> answers) to the row materialisers
/// (<see cref="ReaderEnum"/>, <see cref="SqlExecutionManager"/>'s intent path).
/// Keyed by the <see cref="GqlObjectQuery"/> node the mask was computed for —
/// query nodes are per-request, so nothing crosses requests, and the weak
/// reference lets a finished request's nodes collect. A masked column is
/// selection-only: the transformer pass has already refused it in filter,
/// sort, and aggregate positions before anything is recorded here.
/// </summary>
internal static class ColumnMaskRegistry
{
    private static readonly ConditionalWeakTable<GqlObjectQuery, MaskSetHolder> Masks = new();

    /// <summary>
    /// Records the masked columns (DB names) per table for one query node.
    /// Called once per node per request by the transformer pass.
    /// </summary>
    public static void Set(GqlObjectQuery node, IReadOnlyDictionary<IDbTable, IReadOnlySet<string>> maskedByTable)
    {
        if (maskedByTable.Count == 0)
            return;
        Masks.Remove(node);
        Masks.Add(node, new MaskSetHolder(maskedByTable));
    }

    /// <summary>
    /// The masked column DB names for <paramref name="table"/> on this query
    /// node, or null when nothing is masked.
    /// </summary>
    public static IReadOnlySet<string>? For(GqlObjectQuery node, IDbTable table)
    {
        return Masks.TryGetValue(node, out var holder) && holder.Map.TryGetValue(table, out var set)
            ? set
            : null;
    }

    private sealed class MaskSetHolder
    {
        public MaskSetHolder(IReadOnlyDictionary<IDbTable, IReadOnlySet<string>> map) => Map = map;
        public IReadOnlyDictionary<IDbTable, IReadOnlySet<string>> Map { get; }
    }
}

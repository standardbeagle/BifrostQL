using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Transforms queries by injecting additional filters.
/// Used for mandatory query modifications like tenant isolation, soft-delete filtering, row-level security.
/// Transformers are applied in Priority order (lower = applied first, becomes innermost filter).
/// </summary>
public interface IFilterTransformer
{
    /// <summary>
    /// Lower priority transformers are applied first (innermost).
    /// Recommended ranges:
    /// - 0-99: Security (tenant, RLS) - must be innermost
    /// - 100-199: Data filtering (soft-delete)
    /// - 200+: Application-specific
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Determines if this transformer applies to the given table.
    /// Check table metadata, schema, or other properties.
    /// </summary>
    bool AppliesTo(IDbTable table, QueryTransformContext context);

    /// <summary>
    /// Returns an additional filter to AND with the existing query filter.
    /// Return null if no filter should be added for this query.
    /// Throw to abort the query (e.g., missing required context like tenant ID).
    /// </summary>
    TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context);
}

/// <summary>
/// Context available to filter transformers during query transformation.
/// </summary>
public sealed class QueryTransformContext
{
    public required IDbModel Model { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
    public required QueryType QueryType { get; init; }

    /// <summary>
    /// Path of the current query in the GraphQL hierarchy (e.g., "orders->items").
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// True if this is a nested query (via join/link), false if top-level.
    /// </summary>
    public bool IsNestedQuery { get; init; }
}

/// <summary>
/// Composite wrapper for multiple filter transformers.
/// Applies transformers in priority order and combines their filters.
/// </summary>
public interface IFilterTransformers : IReadOnlyCollection<IFilterTransformer>
{
    /// <summary>
    /// Gets combined filter from all applicable transformers.
    /// Filters are ANDed together in priority order.
    /// </summary>
    TableFilter? GetCombinedFilter(IDbTable table, QueryTransformContext context);
}

public sealed class FilterTransformersWrap : IFilterTransformers
{
    public IReadOnlyCollection<IFilterTransformer> Transformers { get; init; } = Array.Empty<IFilterTransformer>();

    public int Count => Transformers.Count;

    public TableFilter? GetCombinedFilter(IDbTable table, QueryTransformContext context)
    {
        var applicableTransformers = Transformers
            .Where(t => t.AppliesTo(table, context))
            .OrderBy(t => t.Priority);

        TableFilter? combined = null;
        foreach (var transformer in applicableTransformers)
        {
            var filter = transformer.GetAdditionalFilter(table, context);
            if (filter == null) continue;

            combined = combined == null
                ? filter
                : CombineFilters(combined, filter);
        }

        return combined;
    }

    private static TableFilter CombineFilters(TableFilter existing, TableFilter additional)
    {
        // Create an AND filter combining both
        return new TableFilter
        {
            And = new List<TableFilter> { existing, additional },
            FilterType = FilterType.And,
        };
    }

    public IEnumerator<IFilterTransformer> GetEnumerator() => Transformers.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Factory for creating TableFilter instances for common patterns.
/// </summary>
public static class TableFilterFactory
{
    /// <summary>
    /// Creates an equality filter: column = value
    /// </summary>
    public static TableFilter Equals(string tableName, string columnName, object? value)
    {
        return new TableFilter
        {
            TableName = tableName,
            ColumnName = columnName,
            FilterType = FilterType.Join,
            Next = new TableFilter
            {
                RelationName = "_eq",
                Value = value,
                FilterType = FilterType.Relation,
            }
        };
    }

    /// <summary>
    /// Creates an IS NULL filter: column IS NULL
    /// </summary>
    public static TableFilter IsNull(string tableName, string columnName)
    {
        return Equals(tableName, columnName, null);
    }

    /// <summary>
    /// Creates an IS NOT NULL filter: column IS NOT NULL
    /// </summary>
    public static TableFilter IsNotNull(string tableName, string columnName)
    {
        return new TableFilter
        {
            TableName = tableName,
            ColumnName = columnName,
            FilterType = FilterType.Join,
            Next = new TableFilter
            {
                RelationName = "_neq",
                Value = null,
                FilterType = FilterType.Relation,
            }
        };
    }
}

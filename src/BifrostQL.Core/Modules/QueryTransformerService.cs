using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Service that applies filter transformers to queries.
/// </summary>
public interface IQueryTransformerService
{
    /// <summary>
    /// Applies all registered filter transformers to a query and its nested queries.
    /// </summary>
    void ApplyTransformers(
        GqlObjectQuery query,
        IDbModel model,
        IDictionary<string, object?> userContext);
}

public sealed class QueryTransformerService : IQueryTransformerService
{
    private readonly IFilterTransformers _filterTransformers;

    public QueryTransformerService(IFilterTransformers filterTransformers)
    {
        _filterTransformers = filterTransformers;
    }

    public void ApplyTransformers(
        GqlObjectQuery query,
        IDbModel model,
        IDictionary<string, object?> userContext)
    {
        ApplyTransformersRecursive(query, model, userContext, isNested: false);
    }

    private void ApplyTransformersRecursive(
        GqlObjectQuery query,
        IDbModel model,
        IDictionary<string, object?> userContext,
        bool isNested)
    {
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = userContext,
            QueryType = query.QueryType,
            Path = query.Path,
            IsNestedQuery = isNested
        };

        // Column-level read enforcement. IFilterTransformer only sees the table,
        // so transformers that enforce column-read-deny (the policy engine)
        // implement IColumnReadGuard and are called here with the columns this
        // query actually selects. Same reject mechanism as GetAdditionalFilter —
        // a denied column aborts the query rather than being silently stripped.
        EnforceColumnReadGuards(query, context);

        // Get additional filters from transformers
        var additionalFilter = _filterTransformers.GetCombinedFilter(query.DbTable, context);

        if (additionalFilter != null)
        {
            // Combine with existing filter
            query.Filter = query.Filter == null
                ? additionalFilter
                : CombineFilters(query.Filter, additionalFilter);
        }

        // Recursively apply to joined/linked tables
        foreach (var join in query.Joins)
        {
            ApplyTransformersRecursive(join.ConnectedTable, model, userContext, isNested: true);
        }
    }

    private static TableFilter CombineFilters(TableFilter existing, TableFilter additional)
    {
        return new TableFilter
        {
            And = new List<TableFilter> { existing, additional },
            FilterType = FilterType.And,
        };
    }

    private void EnforceColumnReadGuards(GqlObjectQuery query, QueryTransformContext context)
    {
        var requestedColumns = query.ScalarColumns
            .Select(c => c.DbDbName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (requestedColumns.Length == 0)
            return;

        foreach (var guard in _filterTransformers.OfType<IColumnReadGuard>())
            guard.AssertColumnsReadable(query.DbTable, requestedColumns, context);
    }
}

/// <summary>
/// No-op implementation when no transformers are registered.
/// </summary>
public sealed class NullQueryTransformerService : IQueryTransformerService
{
    public static readonly NullQueryTransformerService Instance = new();

    public void ApplyTransformers(
        GqlObjectQuery query,
        IDbModel model,
        IDictionary<string, object?> userContext)
    {
        // No-op
    }
}

using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
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
        // Scope this node's module query arguments (e.g. _includeDeleted) into the
        // user context under table-scoped keys before computing its filter, so a
        // module filter transformer honors arguments supplied on this very node —
        // nested join fields included, not just the root. The root field's args
        // are also captured here (idempotent with the field-context capture).
        foreach (var moduleArg in query.ModuleQueryArguments)
            userContext[ModuleApiRegistry.ScopedKey(moduleArg.Key, query.DbTable)] = moduleArg.Value;

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

        // Aggregate columns (`_agg`) join to destination tables through their own
        // INNER JOIN chain that never passes through query.Joins, so recursing
        // Joins alone leaves those joins unfiltered — a tenant/soft-delete bypass.
        // Compute each linked destination table's combined filter and hand it to
        // the aggregate column so it can scope every join level.
        foreach (var aggregate in query.AggregateColumns)
        {
            aggregate.LinkFilters.Clear();
            foreach (var (direction, link) in aggregate.Links)
            {
                var destinationTable = direction == LinkDirection.ManyToOne ? link.ParentTable : link.ChildTable;
                var destinationContext = new QueryTransformContext
                {
                    Model = model,
                    UserContext = userContext,
                    QueryType = query.QueryType,
                    Path = query.Path,
                    IsNestedQuery = true,
                };
                aggregate.LinkFilters.Add(_filterTransformers.GetCombinedFilter(destinationTable, destinationContext));
            }
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
            .SelectMany(c => ReadGuardColumnNames(query.DbTable, c))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requestedColumns.Length == 0)
            return;

        foreach (var guard in _filterTransformers.OfType<IColumnReadGuard>())
            guard.AssertColumnsReadable(query.DbTable, requestedColumns, context);
    }

    private static IEnumerable<string> ReadGuardColumnNames(IDbTable table, GqlObjectColumn column)
    {
        yield return column.DbDbName;

        if (column.ComputedColumn == null)
            yield break;

        var dependencies = column.ComputedColumn.Dependencies.Count == 0
            ? table.KeyColumns.Select(c => c.DbName)
            : column.ComputedColumn.Dependencies.Select(d => ComputedColumnDefinition.ResolveDependencyColumn(table, d));

        foreach (var dependency in dependencies)
            yield return dependency;
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

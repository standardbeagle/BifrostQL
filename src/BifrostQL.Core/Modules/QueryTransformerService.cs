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
        //
        // The scope is table-keyed but the user context is shared across the whole
        // request, so a sibling field over the SAME table would otherwise inherit
        // this node's args (e.g. one field's _includeDeleted leaking onto the other).
        // Save the prior value of each scoped key and restore it once this node and
        // its subtree are done, so the scope lives exactly for this node's lifetime.
        var savedScopes = new List<(string Key, bool Existed, object? Prior)>();
        foreach (var moduleArg in query.ModuleQueryArguments)
        {
            var scopedKey = ModuleApiRegistry.ScopedKey(moduleArg.Key, query.DbTable);
            var existed = userContext.TryGetValue(scopedKey, out var prior);
            savedScopes.Add((scopedKey, existed, prior));
            userContext[scopedKey] = moduleArg.Value;
        }

        try
        {
            ApplyTransformersToNode(query, model, userContext, isNested);
        }
        finally
        {
            foreach (var (key, existed, prior) in savedScopes)
            {
                if (existed)
                    userContext[key] = prior;
                else
                    userContext.Remove(key);
            }
        }
    }

    private void ApplyTransformersToNode(
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
        // implement IColumnReadGuard and are called here with every column this
        // query node references — not just the columns it selects for output.
        // A caller denied read on a column could otherwise still filter on it
        // (`salary: { _gt: 100000 }`) or sort by it (`_order: { salary: asc }`)
        // and use the boolean result-set/ordering as an oracle to exfiltrate the
        // value. Same reject mechanism as GetAdditionalFilter — a denied column
        // aborts the query rather than being silently stripped.
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
        var guards = _filterTransformers.OfType<IColumnReadGuard>().ToArray();
        if (guards.Length == 0)
            return;

        // Columns are collected per-table (a filter can traverse a SingleLinks
        // relationship into a different table entirely), so each table's set is
        // asserted against that table's own policy rather than the query node's.
        var columnsByTable = new Dictionary<IDbTable, HashSet<string>>();

        void Add(IDbTable table, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (!columnsByTable.TryGetValue(table, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                columnsByTable[table] = set;
            }
            set.Add(name);
        }

        void AddRange(IDbTable table, IEnumerable<string?> names)
        {
            foreach (var name in names)
                Add(table, name);
        }

        // Selected/output columns (scalar + computed-column dependencies).
        AddRange(query.DbTable, query.ScalarColumns.SelectMany(c => ReadGuardColumnNames(query.DbTable, c)));

        // Filter (`filter` / WHERE) columns, including relationship traversals.
        CollectFilterColumns(query.Filter, query.DbTable, Add);

        // Sort (`_order`) columns. Tokens are "<GraphQlName>_asc" / "..._desc".
        AddRange(query.DbTable, query.Sort.Select(s => ResolveColumnDbName(query.DbTable, StripSortSuffix(s))));

        // Aggregate (`_agg`) value columns resolve against the final linked
        // table in the aggregate's join chain, mirroring the destination-table
        // resolution used for aggregate link filters above.
        foreach (var aggregate in query.AggregateColumns)
        {
            if (aggregate.Links.Count == 0)
                continue;

            var (direction, link) = aggregate.Links[^1];
            var targetTable = direction == LinkDirection.ManyToOne ? link.ParentTable : link.ChildTable;
            Add(targetTable, ResolveColumnDbName(targetTable, aggregate.FinalColumnName));
        }

        // GROUP BY aggregate (`<table>Aggregate`) group-key and value columns live
        // directly on the queried table. They must clear the same read guard as
        // scalar/filter/sort/_agg columns, or a policy-denied column could be
        // grouped by or aggregated (SUM/AVG/MIN/MAX) through the aggregate surface —
        // using the group partition or the aggregate value as an exfiltration oracle.
        if (query.GroupedAggregate is { } grouped)
        {
            AddRange(query.DbTable, grouped.GroupColumns.Select(g => g.Column.DbName));
            AddRange(query.DbTable, grouped.ValueColumns.Select(v => v.Column.DbName));
        }

        foreach (var (table, columns) in columnsByTable)
        {
            if (columns.Count == 0)
                continue;

            var names = columns.ToArray();
            foreach (var guard in guards)
                guard.AssertColumnsReadable(table, names, context);
        }
    }

    /// <summary>
    /// Recursively walks a filter tree collecting the columns it references,
    /// attributing each to the table it actually lives on. A leaf comparison
    /// (<c>Next.Next == null</c>) names a column on <paramref name="table"/>;
    /// a deeper chain (<c>Next.Next != null</c>) means <c>ColumnName</c> is a
    /// <see cref="IDbTable.SingleLinks"/> relationship name into another table
    /// (mirrors <see cref="TableFilter.RenderParts"/>'s own traversal), so the
    /// remaining chain is attributed to that linked table instead.
    /// </summary>
    private static void CollectFilterColumns(TableFilter? filter, IDbTable table, Action<IDbTable, string?> add)
    {
        if (filter == null)
            return;

        if (filter.Next == null)
        {
            foreach (var branch in filter.And)
                CollectFilterColumns(branch, table, add);
            foreach (var branch in filter.Or)
                CollectFilterColumns(branch, table, add);
            return;
        }

        if (filter.Next.Next == null)
        {
            add(table, ResolveColumnDbName(table, filter.ColumnName));
            return;
        }

        if (table.SingleLinks.TryGetValue(filter.ColumnName, out var link))
            CollectFilterColumns(filter.Next, link.ParentTable, add);
    }

    /// <summary>
    /// Resolves a filter/sort column reference to its DB name, tolerant of both
    /// name spaces exactly like <see cref="TableFilter.RenderParts"/>: user
    /// filters/sorts key by GraphQL name, but security transformers build
    /// filters keyed by the raw DB column name.
    /// </summary>
    private static string? ResolveColumnDbName(IDbTable table, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return null;

        if (table.GraphQlLookup.TryGetValue(columnName, out var byGraphQl))
            return byGraphQl.DbName;

        if (table.ColumnLookup.TryGetValue(columnName, out var byDb))
            return byDb.DbName;

        return null;
    }

    /// <summary>
    /// Strips the "_asc"/"_desc" suffix from a sort token, mirroring
    /// <c>GqlObjectQuery.RenderSortColumns</c>'s own suffix parsing.
    /// </summary>
    private static string StripSortSuffix(string token)
    {
        if (token.EndsWith("_asc")) return token[..^4];
        if (token.EndsWith("_desc")) return token[..^5];
        return token;
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

using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

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

    // Optional (fail-closed) key manager for the blind-index equality rewrite. A
    // singleton, resolved by DI when field-level encryption is configured; null when it
    // is not, in which case an equality on an encrypted+blind-indexed column is rejected
    // rather than executed as a raw predicate. Same optional-DI shape as the write path.
    private readonly EnvelopeKeyManager? _keyManager;

    public QueryTransformerService(IFilterTransformers filterTransformers, EnvelopeKeyManager? keyManager = null)
    {
        _filterTransformers = filterTransformers;
        _keyManager = keyManager;
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

        // Field-level encryption equality routing. BEFORE the read guard rejects a
        // filter on an encrypted column, transparently rewrite an `_eq`/`_in` predicate
        // on an encrypted column that carries a `blind-index` sibling into an
        // equality/IN on that sibling column, deriving the search token with the SAME
        // derivation as encrypt-on-write. Every other operator on an encrypted column,
        // and an encrypted column without a sibling, is left in place so the guard below
        // still rejects it — the oracle guard is not weakened.
        query.Filter = RewriteBlindIndexEquality(query.Filter, query.DbTable);

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
            for (var i = 0; i < aggregate.Links.Count; ++i)
            {
                var (direction, link) = aggregate.Links[i];
                var destinationTable = direction == LinkDirection.ManyToOne ? link.ParentTable : link.ChildTable;
                var destinationContext = new QueryTransformContext
                {
                    Model = model,
                    UserContext = userContext,
                    QueryType = query.QueryType,
                    Path = query.Path,
                    IsNestedQuery = true,
                };
                var transformed = _filterTransformers.GetCombinedFilter(destinationTable, destinationContext);
                var declared = i < aggregate.DeclaredLinkFilters.Count
                    ? aggregate.DeclaredLinkFilters[i]
                    : null;
                aggregate.LinkFilters.Add(declared is null
                    ? transformed
                    : transformed is null
                        ? declared
                        : CombineFilters(declared, transformed));
            }
        }

        // A filter can TRAVERSE a relationship into another table
        // (`comments(filter: { posts: { title: {_eq: …} } })`), which renders a
        // sub-query over that PARENT table. That sub-query needs the parent's own
        // row scoping, or the caller matches child rows through parent rows it
        // cannot see and reads the parent back out of the child result set.
        // query.Filter is walked AFTER the node's own filter was ANDed in above, so
        // a transformer that itself emits a relationship-shaped filter gets its
        // traversal scoped on the same pass.
        ScopeFilterTraversals(query.Filter, query.DbTable, model, userContext, query);

        // Recursively apply to joined/linked tables
        foreach (var join in query.Joins)
        {
            ApplyTransformersRecursive(join.ConnectedTable, model, userContext, isNested: true);
        }
    }

    /// <summary>
    /// Walks a filter tree and attaches each relationship traversal's TRAVERSED
    /// table's combined transformer filter to that node, for the SQL renderer to AND
    /// into the sub-query. The traversal decision uses
    /// <see cref="TableFilter.IsLeafColumnPredicate"/> — the same predicate the
    /// renderer and the column-guard collector use — so the scoped set and the
    /// emitted SQL cannot diverge.
    /// </summary>
    private void ScopeFilterTraversals(
        TableFilter? filter,
        IDbTable table,
        IDbModel model,
        IDictionary<string, object?> userContext,
        GqlObjectQuery query)
    {
        if (filter == null)
            return;

        if (filter.Next == null)
        {
            foreach (var branch in filter.And)
                ScopeFilterTraversals(branch, table, model, userContext, query);
            foreach (var branch in filter.Or)
                ScopeFilterTraversals(branch, table, model, userContext, query);
            return;
        }

        if (filter.IsLeafColumnPredicate)
            return;

        if (!table.SingleLinks.TryGetValue(filter.ColumnName, out var link))
            return;

        var traversedContext = new QueryTransformContext
        {
            Model = model,
            UserContext = userContext,
            QueryType = query.QueryType,
            Path = query.Path,
            IsNestedQuery = true,
        };
        filter.TraversedTableFilter = _filterTransformers.GetCombinedFilter(link.ParentTable, traversedContext);

        ScopeFilterTraversals(filter.Next, link.ParentTable, model, userContext, query);
    }

    private static TableFilter CombineFilters(TableFilter existing, TableFilter additional) =>
        TableFilter.CombineAnd(existing, additional);

    /// <summary>
    /// Rewrites routable equality predicates on encrypted columns onto their blind-index
    /// sibling. Walks the filter tree mirroring <see cref="CollectFilterColumns"/>'s own
    /// traversal (AND/OR wrappers, leaf predicates, and SingleLinks relationship chains
    /// attributed to the linked table) so a rewrite lands on the same column the guard
    /// would otherwise reject. Returns the (possibly replaced) node so the caller can
    /// splice it back in — a leaf whose target column changes is a brand-new node because
    /// <see cref="TableFilter.ColumnName"/> is init-only.
    /// </summary>
    private TableFilter? RewriteBlindIndexEquality(TableFilter? filter, IDbTable table)
    {
        if (filter is null)
            return null;

        // AND/OR wrapper: recurse into each branch, replacing it in place.
        if (filter.Next is null)
        {
            for (var i = 0; i < filter.And.Count; i++)
                filter.And[i] = RewriteBlindIndexEquality(filter.And[i], table)!;
            for (var i = 0; i < filter.Or.Count; i++)
                filter.Or[i] = RewriteBlindIndexEquality(filter.Or[i], table)!;
            return filter;
        }

        // Leaf predicate: `column: { _op: value }` — Next is the terminal operator node.
        // Use the SHARED IsLeafColumnPredicate, not `Next.Next is null`: the latter is TRUE
        // for a relationship node whose child is an AND wrapper of sibling predicates
        // (customer: { ssn: {_eq}, active: {_eq} }), so it mis-classified that relationship
        // as a leaf, failed the column lookup, and never routed the nested encrypted _eq to
        // its blind-index sibling — the query then fell to the filter guard. This is the same
        // divergence the collector already fixed (see TableFilter.IsLeafColumnPredicate).
        if (filter.IsLeafColumnPredicate)
            return RewriteLeafPredicate(filter, table);

        // Relationship chain: ColumnName names a SingleLinks relationship into another
        // table; the remaining chain is attributed to that table.
        if (table.SingleLinks.TryGetValue(filter.ColumnName, out var link))
            filter.Next = RewriteBlindIndexEquality(filter.Next, link.ParentTable);

        return filter;
    }

    private TableFilter RewriteLeafPredicate(TableFilter leaf, IDbTable table)
    {
        // Resolve the leaf column tolerant of both name spaces; unknown columns are
        // left untouched (the render path will surface a clear error).
        var column = table.GraphQlLookup.TryGetValue(leaf.ColumnName, out var byGraphQl) ? byGraphQl
            : table.ColumnLookup.TryGetValue(leaf.ColumnName, out var byDb) ? byDb
            : null;
        if (column is null)
            return leaf;

        // Only encrypted columns participate; a plain column passes through unchanged.
        if (string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
            return leaf;

        var op = leaf.Next!.RelationName;

        // Only equality/IN route. `_eq: null` is an IS NULL check, not a value search —
        // routing it would hash the empty string and match the wrong rows, so it (and
        // every other operator) is left in place for the guard to reject.
        var isRoutableEq = op == FilterOperators.Eq && leaf.Next.Value is not null;
        var isRoutableIn = op == FilterOperators.In;
        if (!isRoutableEq && !isRoutableIn)
            return leaf;

        // Equality is routable strictly when a blind-index sibling exists to route it to;
        // an encrypted column without one still hits the guard (no partial oracle).
        var blindIndexColumn = column.GetMetadataValue(MetadataKeys.Crypto.BlindIndex);
        if (string.IsNullOrWhiteSpace(blindIndexColumn))
            return leaf;

        // Fail closed: without a resolvable key manager or key-ref no token can be
        // derived — reject rather than emit a raw predicate on ciphertext or the _bidx
        // column.
        var keyRef = column.GetMetadataValue(MetadataKeys.Crypto.KeyRef);
        if (_keyManager is null || string.IsNullOrWhiteSpace(keyRef))
            throw new BifrostExecutionError(EncryptedColumnReadGuard.FilterDeniedMessage)
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };

        // Derive the search token(s) with the IDENTICAL derivation as the write path.
        // Bind the token to THIS physical column (schema, table, column) — identical to the
        // write path's binding — so the derived index key is column-specific (no cross-column
        // oracle) yet write and read still agree.
        object? routedValue = isRoutableEq
            ? BlindIndexComputer.ComputeSearchToken(
                _keyManager, keyRef, table.TableSchema, table.DbName, column.ColumnName, leaf.Next.Value)
            : ((leaf.Next.Value as IEnumerable<object?>) ?? Array.Empty<object?>())
                .Select(v => (object?)BlindIndexComputer.ComputeSearchToken(
                    _keyManager, keyRef, table.TableSchema, table.DbName, column.ColumnName, v))
                .ToList();

        // Replace the predicate: target the blind-index sibling with the token(s).
        return new TableFilter
        {
            TableName = leaf.TableName,
            ColumnName = blindIndexColumn,
            FilterType = FilterType.Join,
            Next = new TableFilter
            {
                RelationName = op,
                Value = routedValue,
                FilterType = FilterType.Relation,
            },
        };
    }

    private void EnforceColumnReadGuards(GqlObjectQuery query, QueryTransformContext context)
    {
        var guards = _filterTransformers.OfType<IColumnReadGuard>().ToArray();
        var filterGuards = _filterTransformers.OfType<IColumnFilterGuard>().ToArray();
        if (guards.Length == 0 && filterGuards.Length == 0)
            return;

        // Columns are collected per-table (a filter can traverse a SingleLinks
        // relationship into a different table entirely), so each table's set is
        // asserted against that table's own policy rather than the query node's.
        // Two sets: every referenced column (read guard) and only the non-output
        // filter/sort/aggregate columns (filter guard). A column used ONLY for output
        // is in the first set, not the second — so an encrypted column can be selected
        // (then decrypted/masked on read) but not used as a query predicate.
        var columnsByTable = new Dictionary<IDbTable, HashSet<string>>();
        var filteredByTable = new Dictionary<IDbTable, HashSet<string>>();

        static void AddTo(Dictionary<IDbTable, HashSet<string>> map, IDbTable table, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (!map.TryGetValue(table, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[table] = set;
            }
            set.Add(name);
        }

        // Read-only (output) column.
        void AddRead(IDbTable table, string? name) => AddTo(columnsByTable, table, name);
        // Column used as a filter/sort/aggregate predicate — counts for BOTH guards.
        void AddFiltered(IDbTable table, string? name)
        {
            AddTo(columnsByTable, table, name);
            AddTo(filteredByTable, table, name);
        }

        // Selected/output columns (scalar + computed-column dependencies) — read only.
        foreach (var name in query.ScalarColumns.SelectMany(c => ReadGuardColumnNames(query.DbTable, c)))
            AddRead(query.DbTable, name);

        // Filter (`filter` / WHERE) columns, including relationship traversals.
        CollectFilterColumns(query.Filter, query.DbTable, AddFiltered);

        // Predicate-position columns a non-GraphQL-tree surface declares directly
        // (the pivot's GROUP BY / aggregated / distinct-discovery columns). Same
        // position as sort/_agg columns, so the same two guards.
        foreach (var name in query.PredicateColumns)
            AddFiltered(query.DbTable, name);

        // Sort (`_order`) columns. Tokens are "<GraphQlName>_asc" / "..._desc".
        foreach (var s in query.Sort)
            AddFiltered(query.DbTable, ResolveColumnDbName(query.DbTable, StripSortSuffix(s)));

        // Aggregate (`_agg`) value columns resolve against the final linked
        // table in the aggregate's join chain, mirroring the destination-table
        // resolution used for aggregate link filters above.
        foreach (var aggregate in query.AggregateColumns)
        {
            if (aggregate.Links.Count == 0)
                continue;

            for (var i = 0; i < aggregate.Links.Count; ++i)
            {
                var (filterDirection, filterLink) = aggregate.Links[i];
                var filterTable = filterDirection == LinkDirection.ManyToOne
                    ? filterLink.ParentTable
                    : filterLink.ChildTable;
                var declaredFilter = i < aggregate.DeclaredLinkFilters.Count
                    ? aggregate.DeclaredLinkFilters[i]
                    : null;
                CollectFilterColumns(declaredFilter, filterTable, AddFiltered);
            }

            var (direction, link) = aggregate.Links[^1];
            var targetTable = direction == LinkDirection.ManyToOne ? link.ParentTable : link.ChildTable;
            AddFiltered(targetTable, ResolveColumnDbName(targetTable, aggregate.FinalColumnName));
        }

        // GROUP BY aggregate (`<table>Aggregate`) group-key and value columns live
        // directly on the queried table. They must clear the same guards as
        // scalar/filter/sort/_agg columns, or a policy-denied (or encrypted) column
        // could be grouped by or aggregated (SUM/AVG/MIN/MAX) through the aggregate
        // surface — using the group partition or the aggregate value as an oracle.
        if (query.GroupedAggregate is { } grouped)
        {
            foreach (var g in grouped.GroupColumns)
                AddFiltered(query.DbTable, g.Column.DbName);
            foreach (var v in grouped.ValueColumns)
                AddFiltered(query.DbTable, v.Column.DbName);
        }

        foreach (var (table, columns) in columnsByTable)
        {
            if (columns.Count == 0)
                continue;

            var names = columns.ToArray();
            foreach (var guard in guards)
                guard.AssertColumnsReadable(table, names, context);
        }

        foreach (var (table, columns) in filteredByTable)
        {
            if (columns.Count == 0)
                continue;

            var names = columns.ToArray();
            foreach (var guard in filterGuards)
                guard.AssertColumnsFilterable(table, names, context);
        }
    }

    /// <summary>
    /// Recursively walks a filter tree collecting the columns it references,
    /// attributing each to the table it actually lives on. The leaf-vs-traversal
    /// decision uses <see cref="TableFilter.IsLeafColumnPredicate"/> — the SAME
    /// predicate <see cref="TableFilter.RenderParts"/> uses to decide whether to
    /// emit a WHERE comparison or a relationship INNER JOIN. A leaf names a column
    /// on <paramref name="table"/>; anything else means <c>ColumnName</c> is a
    /// <see cref="IDbTable.SingleLinks"/> relationship name into another table, so
    /// the remaining sub-filter is walked against that linked table instead.
    ///
    /// The two used to decide this differently (<c>Next.Next == null</c> here vs
    /// the node type there), which silently un-guarded every column referenced
    /// through a relationship filter carrying two or more sibling predicates —
    /// the guard set and the emitted SQL must be derived from one predicate.
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

        if (filter.IsLeafColumnPredicate)
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

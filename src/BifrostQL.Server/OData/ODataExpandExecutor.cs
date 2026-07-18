using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The materialized result of one planned <c>$expand</c> navigation, aligned to the page's root
    /// rows: for each root row, the target rows that belong to it (0 or 1 for a to-one navigation, 0
    /// or more for a to-many). The writer decides the JSON shape from <see cref="IsCollection"/> — a
    /// to-one with no match renders as JSON <c>null</c>, a to-many with no match as an empty array.
    /// </summary>
    /// <param name="Name">The navigation property name written on each root object.</param>
    /// <param name="IsCollection">True for a to-many navigation (array shape), false for to-one.</param>
    /// <param name="Columns">The target entity's identity-filtered columns to project per target row.</param>
    /// <param name="RowValues">Per root row (same order as the page), the matched target rows.</param>
    internal sealed record ODataExpansion(
        string Name,
        bool IsCollection,
        IReadOnlyList<ColumnDto> Columns,
        IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowValues);

    /// <summary>
    /// Executes a one-level <c>$expand</c> plan as independent, fully-scoped read intents — one child
    /// intent per navigation — through <see cref="IQueryIntentExecutor"/>. Each expanded (target)
    /// entity therefore receives the SAME tenant / soft-delete / column-policy transformer pass as
    /// the root read (the pipeline narrows every intent from the caller's identity; the adapter
    /// builds no security predicate). A target row hidden from the caller — wrong tenant, soft-
    /// deleted, policy-denied — is simply absent from the child result set, so it cannot appear
    /// inside the expand of a visible parent (.claude/rules/protocol-adapter-security.md, read seam
    /// invariant + criterion 2).
    ///
    /// <para>The relationship binding is schema-derived: the target rows are fetched with a single
    /// <c>_in</c> predicate over the plan's schema-resolved target key column (the mirror of the
    /// root key column), never a hand-rolled join and never a composite first-column guess (those
    /// are rejected during planning). Fan-out is bounded BEFORE materialization: the child intent is
    /// capped at <c>maxFanout + 1</c> rows and a breach is a deterministic 400, so an expand can
    /// never explode the response past the configured bound (invariant 6).</para>
    /// </summary>
    internal static class ODataExpandExecutor
    {
        public static async Task<IReadOnlyList<ODataExpansion>> ExpandAsync(
            IReadOnlyList<ODataExpandItem> plan,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rootRows,
            IQueryIntentExecutor reads,
            IDictionary<string, object?> userContext,
            string? endpoint,
            int maxFanout,
            CancellationToken cancellationToken)
        {
            if (plan is null) throw new ArgumentNullException(nameof(plan));
            if (rootRows is null) throw new ArgumentNullException(nameof(rootRows));
            if (reads is null) throw new ArgumentNullException(nameof(reads));

            if (plan.Count == 0)
                return Array.Empty<ODataExpansion>();

            var expansions = new List<ODataExpansion>(plan.Count);
            foreach (var item in plan)
                expansions.Add(await ExpandOneAsync(item, rootRows, reads, userContext, endpoint, maxFanout, cancellationToken));
            return expansions;
        }

        private static async Task<ODataExpansion> ExpandOneAsync(
            ODataExpandItem item,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rootRows,
            IQueryIntentExecutor reads,
            IDictionary<string, object?> userContext,
            string? endpoint,
            int maxFanout,
            CancellationToken cancellationToken)
        {
            // Distinct non-null root key values drive the child fetch. A null root key means "no
            // related row" for that parent and contributes nothing to the IN set.
            var rootKeyDb = item.RootKeyColumn.DbName;
            var keyValues = new List<object?>();
            var keySeen = new HashSet<object>();
            foreach (var row in rootRows)
            {
                if (!row.TryGetValue(rootKeyDb, out var v) || v is null)
                    continue;
                if (keySeen.Add(v))
                    keyValues.Add(v);
            }

            var byTargetKey = keyValues.Count == 0
                ? new Dictionary<object, List<IReadOnlyDictionary<string, object?>>>()
                : await FetchAndGroupAsync(item, keyValues, reads, userContext, endpoint, maxFanout, cancellationToken);

            var targetKeyDb = item.TargetKeyColumn.DbName;
            var perRow = new List<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rootRows.Count);
            var empty = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)Array.Empty<IReadOnlyDictionary<string, object?>>();
            foreach (var row in rootRows)
            {
                if (row.TryGetValue(rootKeyDb, out var key) && key is not null
                    && byTargetKey.TryGetValue(key, out var matches))
                    perRow.Add(matches);
                else
                    perRow.Add(empty);
            }

            return new ODataExpansion(item.Name, item.IsCollection, item.Target.Columns, perRow);
        }

        private static async Task<Dictionary<object, List<IReadOnlyDictionary<string, object?>>>> FetchAndGroupAsync(
            ODataExpandItem item,
            IReadOnlyList<object?> keyValues,
            IQueryIntentExecutor reads,
            IDictionary<string, object?> userContext,
            string? endpoint,
            int maxFanout,
            CancellationToken cancellationToken)
        {
            var target = item.Target.Table;
            var query = new GqlObjectQuery
            {
                DbTable = target,
                SchemaName = target.TableSchema,
                TableName = target.DbName,
                GraphQlName = target.GraphQlName,
                Path = target.GraphQlName,
                // The relationship predicate: target key IN (root key values). Expressed as the same
                // parameterized TableFilter shape the $filter path uses, so the values bind as SQL
                // parameters. The pipeline AND-composes this with the target's own tenant/soft-delete/
                // policy scope — this predicate can only ever narrow, never widen, the caller's view.
                Filter = TableFilter.FromObject(
                    new Dictionary<string, object?>
                    {
                        [item.TargetKeyColumn.GraphQlName] =
                            new Dictionary<string, object?> { ["_in"] = keyValues.ToList() },
                    },
                    target.DbName),
                // Bound the fan-out BEFORE materializing: fetch at most one row past the cap so a
                // breach is detectable and rejected, never an unbounded expansion.
                Limit = maxFanout + 1,
            };

            var projected = ProjectTargetColumns(item);
            foreach (var column in projected)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));

            var result = await reads.ExecuteAsync(
                new QueryIntent { Query = query, UserContext = userContext, Endpoint = endpoint },
                cancellationToken);

            if (result.Rows.Count > maxFanout)
                throw ODataProtocolException.BadRequest(
                    $"$expand of '{item.Name}' exceeds the maximum of {maxFanout} related rows.");

            var targetKeyDb = item.TargetKeyColumn.DbName;
            var grouped = new Dictionary<object, List<IReadOnlyDictionary<string, object?>>>();
            foreach (var row in result.Rows)
            {
                if (!row.TryGetValue(targetKeyDb, out var key) || key is null)
                    continue;
                if (!grouped.TryGetValue(key, out var bucket))
                {
                    bucket = new List<IReadOnlyDictionary<string, object?>>();
                    grouped[key] = bucket;
                }
                bucket.Add(row);
            }
            return grouped;
        }

        /// <summary>
        /// The target columns to fetch: the entity's visible projection plus the grouping key column
        /// (added if the projection omits it) so the child rows can be bucketed by their FK/PK. The
        /// key column is guaranteed readable — planning rejected the navigation otherwise.
        /// </summary>
        private static IReadOnlyList<ColumnDto> ProjectTargetColumns(ODataExpandItem item)
        {
            var columns = item.Target.Columns;
            var hasKey = columns.Any(c => string.Equals(c.DbName, item.TargetKeyColumn.DbName, StringComparison.OrdinalIgnoreCase));
            if (hasKey)
                return columns;
            var withKey = new List<ColumnDto>(columns) { item.TargetKeyColumn };
            return withKey;
        }
    }
}

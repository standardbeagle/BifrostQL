using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Resolvers;
using GraphQLParser.AST;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Root resolver for a tracked table's trail read field
    /// (<c>&lt;table&gt;History(limit, offset, sort, filter) { total data { ... } }</c>).
    /// Builds a paged <see cref="GqlObjectQuery"/> over the tracked table's resolved
    /// history TARGET from the field's arguments and selection set, then hands it to
    /// <see cref="ISqlExecutionManager.ResolveHistoryTrailAsync"/>, which applies the
    /// entity discriminator, the authorization predicates, and the standard filter
    /// transformer pass before any SQL runs — the resolver only parses the request,
    /// so no argument shape can bypass those. Wired directly (not via the join
    /// dispatcher) because the field name is not a table's GraphQL name.
    /// </summary>
    public sealed class HistoryTableResolver : IFieldResolver
    {
        private readonly IDbTable _trackedTable;
        private readonly IDbTable _historyTable;

        public HistoryTableResolver(IDbTable trackedTable, IDbTable historyTable)
        {
            _trackedTable = trackedTable;
            _historyTable = historyTable;
        }

        private string FieldName => HistorySurface.HistoryFieldName(_trackedTable);

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            try
            {
                var query = BuildQuery(context);
                var bifrost = new BifrostContextAdapter(context);
                return await bifrost.Executor.ResolveHistoryTrailAsync(
                    new BifrostFieldContextAdapter(context), _trackedTable, query);
            }
            catch (BifrostExecutionError ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }

        private GqlObjectQuery BuildQuery(IResolveFieldContext context)
        {
            var filterArg = context.GetArgument<Dictionary<string, object?>>("filter");
            var filter = filterArg is { Count: > 0 }
                ? TableFilter.FromObject(filterArg, _historyTable)
                : null;

            return new GqlObjectQuery
            {
                DbTable = _historyTable,
                TableName = _historyTable.DbName,
                SchemaName = _historyTable.TableSchema,
                GraphQlName = FieldName,
                FieldName = FieldName,
                Alias = context.FieldAst.Alias?.Name.StringValue,
                Path = FieldName,
                IncludeResult = true,
                Limit = context.GetArgument<int?>("limit"),
                Offset = context.GetArgument<int?>("offset"),
                Sort = ResolveSort(context),
                Filter = filter,
                ScalarColumns = BuildScalarColumns(context),
            };
        }

        private List<string> ResolveSort(IResolveFieldContext context)
        {
            var rawSort = context.GetArgument<List<object?>>("sort");
            if (rawSort is not { Count: > 0 })
                return new List<string>();

            return rawSort
                .Select(s => s?.ToString()
                    ?? throw new BifrostExecutionError($"Null sort token on '{FieldName}'."))
                .ToList();
        }

        /// <summary>
        /// Collects the trail columns selected under <c>data</c>. Every selection must
        /// be a plain column of the history table: the trail read surface exposes trail
        /// rows only, so a nested selection or a non-column name is rejected with
        /// steering rather than silently returning nothing. Both rejections are
        /// defensive: <c>DbModel.UnlinkHistoryTargets</c> strips every link from a
        /// history target, so its row type publishes plain columns only and document
        /// validation refuses either shape before this resolver runs. Inline fragments
        /// and named fragment spreads are flattened.
        ///
        /// Walked from the raw selection set via <see cref="SelectionWalk"/>, the same
        /// helper the aggregate resolver uses, so EVERY <c>data</c> node contributes
        /// its own sub-selection. Reading
        /// <see cref="IResolveFieldContext.SubFields"/> instead would make the
        /// projection depend on the execution engine merging same-response-key
        /// selection sets before the resolver runs — true today, and the reason the
        /// reported column-dropping defect does not reproduce, but an assumption this
        /// resolver has no reason to hold.
        /// </summary>
        private List<GqlObjectColumn> BuildScalarColumns(IResolveFieldContext context)
        {
            var columns = new List<GqlObjectColumn>();
            if (!SelectionWalk.SelectedFieldNodes(context).TryGetValue("data", out var dataNodes))
                return columns;

            foreach (var dataNode in dataNodes)
                CollectDataColumns(context, dataNode.SelectionSet, columns);
            return columns;
        }

        private void CollectDataColumns(
            IResolveFieldContext context, GraphQLSelectionSet? selectionSet, List<GqlObjectColumn> columns)
        {
            SelectionWalk.Walk(context, selectionSet, field =>
            {
                var name = field.Name.StringValue;
                if (name.StartsWith("__", StringComparison.Ordinal))
                    return;
                if (field.SelectionSet is { Selections.Count: > 0 })
                    throw new BifrostExecutionError(
                        $"'{FieldName}' returns trail rows only; nested field '{name}' is not supported " +
                        "on the history read surface. Select the trail columns directly.");
                if (!_historyTable.GraphQlLookup.TryGetValue(name, out var column))
                    throw new BifrostExecutionError(
                        $"'{name}' is not a trail column of '{FieldName}'; the history read surface " +
                        "supports the history table's plain columns only.");
                columns.Add(new GqlObjectColumn(column.DbName, field.Alias?.Name.StringValue ?? name));
            });
        }
    }
}

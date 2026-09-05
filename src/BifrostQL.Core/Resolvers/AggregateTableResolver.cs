using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Root resolver for a table's GROUP BY aggregate field
    /// (<c>&lt;table&gt;Aggregate(filter, groupBy) { ... }</c>). Builds a grouped
    /// <see cref="GqlObjectQuery"/> from the field's arguments and selection set,
    /// then hands it to <see cref="ISqlExecutionManager.ResolveAggregateAsync"/> which
    /// applies the same filter transformers (tenant isolation, soft-delete) as row
    /// queries before the SQL runs. Wired directly (not via the join dispatcher)
    /// because it needs the raw selection set to know which ops were requested.
    /// </summary>
    public sealed class AggregateTableResolver : IFieldResolver
    {
        private readonly IDbTable _table;

        public AggregateTableResolver(IDbTable table) => _table = table;

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            try
            {
                var bifrost = new BifrostContextAdapter(context);
                var query = BuildQuery(context, bifrost.Model.TypeMapper);
                return await bifrost.Executor.ResolveAggregateAsync(new BifrostFieldContextAdapter(context), _table, query);
            }
            catch (BifrostExecutionError ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }

        private GqlObjectQuery BuildQuery(IResolveFieldContext context, ITypeMapper typeMapper)
        {
            var filterArg = context.GetArgument<Dictionary<string, object?>>("filter");
            var filter = filterArg is { Count: > 0 }
                ? TableFilter.FromObject(filterArg, _table.DbName)
                : null;

            var grouped = new GroupedAggregate
            {
                GroupColumns = ResolveGroupColumns(context),
                IncludeCount = SelectedFieldNodes(context).ContainsKey(AggregateSurface.CountField),
                ValueColumns = ResolveValueColumns(context, typeMapper),
            };

            return new GqlObjectQuery
            {
                DbTable = _table,
                TableName = _table.DbName,
                SchemaName = _table.TableSchema,
                GraphQlName = _table.GraphQlName,
                FieldName = AggregateSurface.AggregateFieldName(_table),
                Alias = context.FieldAst.Alias?.Name?.StringValue,
                Filter = filter,
                GroupedAggregate = grouped,
                // Paging arguments for the GROUP window. They are clamped to the
                // model's max-query-rows ceiling when the SQL is built, so a client
                // can only narrow the window the server already bounds.
                Limit = context.GetArgument<int?>("limit"),
                Offset = context.GetArgument<int?>("offset"),
            };
        }

        /// <summary>
        /// Resolves the <c>groupBy</c> enum members to model columns, preserving
        /// request order. Each member is a schema-derived column-enum value, so it
        /// always maps to a real column; an unmapped value is a schema/client bug and
        /// fails fast rather than reaching SQL.
        /// </summary>
        private IReadOnlyList<AggregateGroupColumn> ResolveGroupColumns(IResolveFieldContext context)
        {
            var groupBy = context.GetArgument<List<object?>>("groupBy");
            if (groupBy is not { Count: > 0 })
                return Array.Empty<AggregateGroupColumn>();

            var result = new List<AggregateGroupColumn>(groupBy.Count);
            foreach (var member in groupBy)
            {
                var graphQlName = member?.ToString()
                    ?? throw new BifrostExecutionError($"Null groupBy column on aggregate of '{_table.GraphQlName}'.");
                if (!_table.GraphQlLookup.TryGetValue(graphQlName, out var column))
                    throw new BifrostExecutionError($"Unknown groupBy column '{graphQlName}' on aggregate of '{_table.GraphQlName}'.");
                result.Add(new AggregateGroupColumn(column, column.GraphQlName));
            }
            return result;
        }

        /// <summary>
        /// Builds the value projections: for every selected op group (<c>_sum</c>, …)
        /// one aggregate over each of THAT op group's selected sub-fields. Columns
        /// the client did not select contribute nothing, so the SQL stays as narrow
        /// as the request and the column read guard never sees (or denies on) an
        /// unselected column — a policy-read-denied sibling must not block
        /// <c>_sum { amount }</c>.
        /// </summary>
        private IReadOnlyList<AggregateValueColumn> ResolveValueColumns(IResolveFieldContext context, ITypeMapper typeMapper)
        {
            var selected = SelectedFieldNodes(context);
            var numericByName = AggregateSurface.NumericColumns(_table, typeMapper)
                .ToDictionary(c => c.GraphQlName, StringComparer.Ordinal);
            var result = new List<AggregateValueColumn>();
            foreach (var (opGroup, operation) in AggregateSurface.ValueOps)
            {
                if (!selected.TryGetValue(opGroup, out var opField))
                    continue;
                foreach (var columnName in SelectedSubFieldNames(context, opField))
                {
                    if (!numericByName.TryGetValue(columnName, out var column))
                        throw new BifrostExecutionError($"Unknown aggregate column '{columnName}' under '{opGroup}' on aggregate of '{_table.GraphQlName}'.");
                    result.Add(new AggregateValueColumn(operation, column, opGroup, AggregateSurface.ValueAlias(opGroup, column.GraphQlName)));
                }
            }
            return result;
        }

        /// <summary>
        /// The AST nodes of the fields selected directly under the aggregate field
        /// (group keys, <c>_count</c>, op groups). Keyed by the field's schema name,
        /// not its response alias, so op-group detection is alias-independent.
        /// </summary>
        private static IReadOnlyDictionary<string, GraphQLParser.AST.GraphQLField> SelectedFieldNodes(IResolveFieldContext context)
        {
            var nodes = new Dictionary<string, GraphQLParser.AST.GraphQLField>(StringComparer.Ordinal);
            if (context.SubFields != null)
                foreach (var sub in context.SubFields.Values)
                    nodes[sub.Field.Name.StringValue] = sub.Field;
            return nodes;
        }

        /// <summary>
        /// The schema field names selected under one op group field, walking inline
        /// fragments and named fragment spreads so fragment-wrapped selections
        /// aggregate exactly the same columns as flat ones.
        /// </summary>
        private static IEnumerable<string> SelectedSubFieldNames(IResolveFieldContext context, GraphQLParser.AST.GraphQLField field)
        {
            var names = new List<string>();
            CollectSubFieldNames(context, field.SelectionSet, names);
            return names;
        }

        private static void CollectSubFieldNames(IResolveFieldContext context, GraphQLParser.AST.GraphQLSelectionSet? selectionSet, List<string> names)
        {
            if (selectionSet == null)
                return;
            foreach (var selection in selectionSet.Selections)
            {
                switch (selection)
                {
                    case GraphQLParser.AST.GraphQLField f:
                        names.Add(f.Name.StringValue);
                        break;
                    case GraphQLParser.AST.GraphQLInlineFragment inline:
                        CollectSubFieldNames(context, inline.SelectionSet, names);
                        break;
                    case GraphQLParser.AST.GraphQLFragmentSpread spread:
                        var fragment = context.Document?.Definitions
                            .OfType<GraphQLParser.AST.GraphQLFragmentDefinition>()
                            .FirstOrDefault(d => d.FragmentName.Name.StringValue == spread.FragmentName.Name.StringValue);
                        if (fragment != null)
                            CollectSubFieldNames(context, fragment.SelectionSet, names);
                        break;
                }
            }
        }
    }
}

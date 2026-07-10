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
                IncludeCount = SelectedFieldNames(context).Contains(AggregateSurface.CountField),
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
        /// one aggregate over each visible numeric column. Op groups the client did
        /// not select contribute nothing, so the SQL stays as narrow as the request.
        /// </summary>
        private IReadOnlyList<AggregateValueColumn> ResolveValueColumns(IResolveFieldContext context, ITypeMapper typeMapper)
        {
            var selected = SelectedFieldNames(context);
            var numericColumns = AggregateSurface.NumericColumns(_table, typeMapper).ToList();
            var result = new List<AggregateValueColumn>();
            foreach (var (opGroup, operation) in AggregateSurface.ValueOps)
            {
                if (!selected.Contains(opGroup))
                    continue;
                foreach (var column in numericColumns)
                    result.Add(new AggregateValueColumn(operation, column, opGroup, AggregateSurface.ValueAlias(opGroup, column.GraphQlName)));
            }
            return result;
        }

        /// <summary>
        /// The schema field names selected directly under the aggregate field
        /// (group keys, <c>_count</c>, op groups). Keyed by the field's schema name,
        /// not its response alias, so op-group detection is alias-independent.
        /// </summary>
        private static HashSet<string> SelectedFieldNames(IResolveFieldContext context)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (context.SubFields != null)
                foreach (var sub in context.SubFields.Values)
                    names.Add(sub.Field.Name.StringValue);
            return names;
        }
    }
}

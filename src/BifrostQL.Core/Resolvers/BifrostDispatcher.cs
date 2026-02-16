using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQL.Utilities;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Universal resolver dispatcher that routes field resolution based on DbModel.
    /// Builds a resolver map from the model and attaches itself as the IFieldResolver
    /// on every field in the schema, delegating to the appropriate IBifrostResolver.
    ///
    /// Also serves as the entry point for protocol-agnostic IBifrostRequest processing.
    /// Any frontend can produce IBifrostRequest intents and convert them to GqlObjectQuery
    /// via <see cref="ToObjectQueries"/>, bypassing the GraphQL AST entirely.
    /// </summary>
    public sealed class BifrostDispatcher : IBifrostResolver, IFieldResolver
    {
        private readonly IDbModel _model;
        private readonly Dictionary<(string typeName, string fieldName), IBifrostResolver> _resolvers;

        public BifrostDispatcher(IDbModel model)
        {
            _model = model;
            _resolvers = BuildResolverMap(model);
        }

        /// <summary>
        /// Converts protocol-agnostic request intents into GqlObjectQuery objects
        /// suitable for the SQL generation pipeline. This is the bridge between
        /// any IProtocolFrontend's parsed output and the existing SQL engine.
        ///
        /// When an IBifrostRequest has a pre-built Filter (e.g., from a non-GraphQL frontend),
        /// it is merged into the generated GqlObjectQuery's filter chain.
        /// </summary>
        public IReadOnlyList<GqlObjectQuery> ToObjectQueries(IReadOnlyList<IBifrostRequest> requests)
        {
            var queries = new List<GqlObjectQuery>(requests.Count);
            foreach (var request in requests)
            {
                var queryField = BifrostRequestAdapter.ToQueryField(request);
                var query = queryField.ToSqlData(_model);
                ApplyPreBuiltFilters(request, query);
                queries.Add(query);
            }
            return queries;
        }

        /// <summary>
        /// Applies pre-built filters from IBifrostRequest to the generated GqlObjectQuery.
        /// This supports non-GraphQL frontends that construct TableFilter directly
        /// instead of passing filter dictionaries through Arguments.
        /// </summary>
        private static void ApplyPreBuiltFilters(IBifrostRequest request, GqlObjectQuery query)
        {
            if (request.Filter == null) return;

            if (query.Filter == null)
            {
                query.Filter = request.Filter;
            }
            else
            {
                query.Filter = new TableFilter
                {
                    And = new List<TableFilter> { query.Filter, request.Filter },
                    FilterType = FilterType.And,
                };
            }
        }

        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            return DbJoinFieldResolver.Instance.ResolveAsync(context);
        }

        async ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            var parentTypeName = context.ParentType.Name;
            var fieldName = context.FieldDefinition.Name;

            try
            {
                if (_resolvers.TryGetValue((parentTypeName, fieldName), out var resolver))
                {
                    if (resolver is IFieldResolver fieldResolver)
                        return await fieldResolver.ResolveAsync(context);
                    return await resolver.ResolveAsync(new BifrostFieldContextAdapter(context));
                }

                return await DbJoinFieldResolver.Instance.ResolveAsync(new BifrostFieldContextAdapter(context));
            }
            catch (BifrostExecutionError ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }

        /// <summary>
        /// Wires this dispatcher as the resolver for all fields via the SchemaBuilder API.
        /// Must be called inside the Schema.For callback (before schema initialization).
        /// </summary>
        public void WireResolvers(SchemaBuilder builder)
        {
            const string queryType = "database";
            const string mutationType = "databaseInput";

            var query = builder.Types.For(queryType);
            var mut = builder.Types.For(mutationType);

            foreach (var table in _model.Tables)
            {
                query.FieldFor(table.GraphQlName).Resolver = this;
                mut.FieldFor(table.GraphQlName).Resolver = this;
                mut.FieldFor($"{table.GraphQlName}_batch").Resolver = this;

                var tableType = builder.Types.For(table.GraphQlName);
                tableType.FieldFor("_agg").Resolver = this;

                foreach (var column in table.Columns)
                    tableType.FieldFor(column.GraphQlName).Resolver = this;

                foreach (var singleLink in table.SingleLinks)
                    tableType.FieldFor(singleLink.Value.ParentTable.GraphQlName).Resolver = this;

                foreach (var multiLink in table.MultiLinks)
                    tableType.FieldFor(multiLink.Value.ChildTable.GraphQlName).Resolver = this;

                foreach (var joinTable in _model.Tables)
                {
                    tableType.FieldFor(joinTable.JoinFieldName).Resolver = this;
                    tableType.FieldFor(joinTable.SingleFieldName).Resolver = this;
                }
            }

            query.FieldFor("_dbSchema").Resolver = this;

            if (SchemaGenerator.IsRawSqlEnabled(_model))
                query.FieldFor("_rawQuery").Resolver = this;

            if (SchemaGenerator.IsGenericTableEnabled(_model))
            {
                query.FieldFor("_table").Resolver = this;

                var genericResultType = builder.Types.For("GenericTableResult");
                foreach (var fieldName in new[] { "tableName", "columns", "rows", "totalCount" })
                    genericResultType.FieldFor(fieldName).Resolver = this;

                var genericColumnType = builder.Types.For("GenericColumnMetadata");
                foreach (var fieldName in new[] { "name", "dataType", "isNullable", "isPrimaryKey" })
                    genericColumnType.FieldFor(fieldName).Resolver = this;
            }

            foreach (var proc in _model.StoredProcedures)
            {
                if (proc.IsReadOnly)
                    query.FieldFor(proc.FullGraphQlName).Resolver = this;
                else
                    mut.FieldFor(proc.FullGraphQlName).Resolver = this;

                var resultType = builder.Types.For(proc.ResultTypeName);
                foreach (var fieldName in new[] { "resultSets", "affectedRows" })
                    resultType.FieldFor(fieldName).Resolver = this;

                foreach (var outputParam in proc.OutputParameters)
                    resultType.FieldFor(outputParam.GraphQlName).Resolver = this;
            }
        }

        private static Dictionary<(string typeName, string fieldName), IBifrostResolver> BuildResolverMap(IDbModel model)
        {
            var map = new Dictionary<(string, string), IBifrostResolver>();

            const string queryType = "database";
            const string mutationType = "databaseInput";

            foreach (var table in model.Tables)
            {
                map[(queryType, table.GraphQlName)] = new DbTableResolver(table);
                map[(mutationType, table.GraphQlName)] = new DbTableMutateResolver(table);
                map[(mutationType, $"{table.GraphQlName}_batch")] = new DbTableBatchResolver(table);
            }

            map[(queryType, "_dbSchema")] = new MetaSchemaResolver(model);

            if (SchemaGenerator.IsRawSqlEnabled(model))
                map[(queryType, "_rawQuery")] = new RawSqlQueryResolver(model);

            if (SchemaGenerator.IsGenericTableEnabled(model))
            {
                var config = GenericTableConfig.FromModel(model);
                map[(queryType, "_table")] = new GenericTableQueryResolver(model, config);
            }

            foreach (var proc in model.StoredProcedures)
            {
                var resolver = new StoredProcedureResolver(proc);
                if (proc.IsReadOnly)
                    map[(queryType, proc.FullGraphQlName)] = resolver;
                else
                    map[(mutationType, proc.FullGraphQlName)] = resolver;
            }

            return map;
        }
    }
}

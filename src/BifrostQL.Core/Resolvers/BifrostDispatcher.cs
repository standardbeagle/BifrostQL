using BifrostQL.Core.Model;
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

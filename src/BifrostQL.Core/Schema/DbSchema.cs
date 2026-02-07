using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;

namespace BifrostQL.Core.Schema
{
    public static class DbSchema
    {

        public static ISchema FromModel(IDbModel model)
        {
            var includeDynamicJoins = model.GetMetadataBool("dynamic-joins", true);
            var schemaText = SchemaGenerator.SchemaTextFromModel(model, includeDynamicJoins);
            var schema = GraphQL.Types.Schema.For<DbSchemaBuilder>(schemaText, _ =>
            {
                var query = _.Types.For("database");
                var mut = _.Types.For("databaseInput");
                foreach (var table in model.Tables)
                {
                    var tableField = query.FieldFor(table.GraphQlName);
                    tableField.Resolver = new DbTableResolver(table);

                    var tableInsertField = mut.FieldFor(table.GraphQlName);
                    tableInsertField.Resolver = new DbTableMutateResolver(table);

                    var tableBatchField = mut.FieldFor($"{table.GraphQlName}_batch");
                    tableBatchField.Resolver = new DbTableBatchResolver(table);

                    var tableType = _.Types.For(table.GraphQlName);
                    var aggregateType = tableType.FieldFor("_agg");
                    aggregateType.Resolver = DbJoinFieldResolver.Instance;


                    foreach (var column in table.Columns)
                    {
                        var columnField = tableType.FieldFor(column.GraphQlName);
                        columnField.Resolver = DbJoinFieldResolver.Instance;
                    };
                    foreach (var singleLink in table.SingleLinks)
                    {
                        var columnField = tableType.FieldFor(singleLink.Value.ParentTable.GraphQlName);
                        columnField.Resolver = DbJoinFieldResolver.Instance;
                    };
                    foreach (var multiLink in table.MultiLinks)
                    {
                        var columnField = tableType.FieldFor(multiLink.Value.ChildTable.GraphQlName);
                        columnField.Resolver = DbJoinFieldResolver.Instance;
                    };
                    foreach (var joinTable in model.Tables)
                    {
                        var joinField = tableType.FieldFor(joinTable.JoinFieldName);
                        joinField.Resolver = DbJoinFieldResolver.Instance;
                        var singleField = tableType.FieldFor(joinTable.SingleFieldName);
                        singleField.Resolver = DbJoinFieldResolver.Instance;
                    }
                }

                var dbSchema = query.FieldFor("_dbSchema");
                dbSchema.Resolver = new MetaSchemaResolver(model);

                if (SchemaGenerator.IsRawSqlEnabled(model))
                {
                    var rawQuery = query.FieldFor("_rawQuery");
                    rawQuery.Resolver = new RawSqlQueryResolver(model);
                }

                if (SchemaGenerator.IsGenericTableEnabled(model))
                {
                    var config = GenericTableConfig.FromModel(model);
                    var genericTableField = query.FieldFor("_table");
                    genericTableField.Resolver = new GenericTableQueryResolver(model, config);

                    var genericResultType = _.Types.For("GenericTableResult");
                    foreach (var fieldName in new[] { "tableName", "columns", "rows", "totalCount" })
                    {
                        genericResultType.FieldFor(fieldName).Resolver = DbJoinFieldResolver.Instance;
                    }

                    var genericColumnType = _.Types.For("GenericColumnMetadata");
                    foreach (var fieldName in new[] { "name", "dataType", "isNullable", "isPrimaryKey" })
                    {
                        genericColumnType.FieldFor(fieldName).Resolver = DbJoinFieldResolver.Instance;
                    }
                }

                foreach (var proc in model.StoredProcedures)
                {
                    var resolver = new StoredProcedureResolver(proc);
                    if (proc.IsReadOnly)
                    {
                        var procField = query.FieldFor(proc.FullGraphQlName);
                        procField.Resolver = resolver;
                    }
                    else
                    {
                        var procField = mut.FieldFor(proc.FullGraphQlName);
                        procField.Resolver = resolver;
                    }

                    var resultType = _.Types.For(proc.ResultTypeName);
                    foreach (var fieldName in new[] { "resultSets", "affectedRows" })
                    {
                        resultType.FieldFor(fieldName).Resolver = DbJoinFieldResolver.Instance;
                    }
                    foreach (var outputParam in proc.OutputParameters)
                    {
                        resultType.FieldFor(outputParam.GraphQlName).Resolver = DbJoinFieldResolver.Instance;
                    }
                }

            });
            return schema;
        }
    }

    //internal record NameMatcher {
    //    public string PrimaryGqlTableName { get; set; }
    //    public string NestedGqlTableName { get; set; }
    //}

    public enum IdentityType
    {
        None,
        Optional,
        Required
    }
}
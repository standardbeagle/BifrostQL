using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    internal static class SchemaGenerator
    {
        public static string SchemaTextFromModel(IDbModel model, bool includeDynamicJoins = true)
        {
            var builder = new StringBuilder();
            var tableGenerators = model.Tables.Select(t => new TableSchemaGenerator(t)).ToList();
            var spGenerators = model.StoredProcedures.Select(p => new StoredProcedureSchemaGenerator(p)).ToList();
            var readOnlySpGenerators = model.StoredProcedures
                .Where(p => p.IsReadOnly)
                .Select(p => new StoredProcedureSchemaGenerator(p)).ToList();
            var mutatingSpGenerators = model.StoredProcedures
                .Where(p => !p.IsReadOnly)
                .Select(p => new StoredProcedureSchemaGenerator(p)).ToList();

            builder.AppendLine("schema { query: database mutation: databaseInput }");
            builder.AppendLine("type database {");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetTableFieldDefinition());
            }
            foreach (var generator in readOnlySpGenerators)
            {
                builder.AppendLine(generator.GetFieldDefinition());
            }
            builder.AppendLine("_dbSchema(graphQlName: String): [dbTableSchema!]!");
            if (IsRawSqlEnabled(model))
            {
                builder.AppendLine("_rawQuery(sql: String!, params: JSON, timeout: Int): [JSON]!");
            }
            if (IsGenericTableEnabled(model))
            {
                builder.AppendLine("_table(name: String!, limit: Int, offset: Int, filter: JSON): GenericTableResult!");
            }
            builder.AppendLine("}");

            foreach (var generator in tableGenerators)
            {
                if (includeDynamicJoins)
                {
                    builder.AppendLine(generator.GetDynamicJoinDefinition(model, false));
                    builder.AppendLine(generator.GetDynamicJoinDefinition(model, true));
                }
                builder.AppendLine(generator.GetTableTypeDefinition(model, includeDynamicJoins));
                builder.AppendLine(generator.GetPagedTableTypeDefinition());
            }

            builder.Append(GetInputAndArgumentTypes(model, tableGenerators, mutatingSpGenerators));

            foreach (var generator in spGenerators)
            {
                builder.AppendLine(generator.GetResultTypeDefinition());
                var inputType = generator.GetInputTypeDefinition();
                if (!string.IsNullOrEmpty(inputType))
                    builder.AppendLine(inputType);
            }

            //Define the filter types of all the columns in the database, needs to be specific to the connected database, and distinct because of GraphQL.
            foreach (var gqlType in model.Tables.SelectMany(t => t.Columns).Select<ColumnDto, string>(c => GetSimpleGraphQlTypeName(c.EffectiveDataType)).Distinct())
            {
                builder.AppendLine(GetFilterType(gqlType));
            }

            builder.AppendLine(GetMetadataSchemaTypes());

            if (IsGenericTableEnabled(model))
            {
                builder.AppendLine(GetGenericTableTypes());
            }

            return builder.ToString();

        }

        internal static string GetMetadataSchemaTypes()
        {
            var sb = new StringBuilder();

            sb.AppendLine("type dbTableSchema {");
            sb.AppendLine("schema: String!");
            sb.AppendLine("dbName: String!");
            sb.AppendLine("graphQlName: String!");
            sb.AppendLine("primaryKeys: [String!]");
            sb.AppendLine("labelColumn: String!");
            sb.AppendLine("isEditable: Boolean!");
            sb.AppendLine("metadata: [dbMetadataSchema!]!");
            sb.AppendLine("multiJoins: [dbJoinSchema!]!");
            sb.AppendLine("singleJoins: [dbJoinSchema!]!");
            sb.AppendLine("columns: [dbColumnSchema!]!");
            sb.AppendLine("}");

            sb.AppendLine("type dbJoinSchema {");
            sb.AppendLine("name: String!");
            sb.AppendLine("sourceColumnNames: [String!]!");
            sb.AppendLine("destinationTable: String!");
            sb.AppendLine("destinationColumnNames: [String!]!");
            sb.AppendLine("metadata: [dbMetadataSchema!]!");
            sb.AppendLine("}");

            sb.AppendLine("type dbColumnSchema {");
            sb.AppendLine("dbName: String!");
            sb.AppendLine("graphQlName: String!");
            sb.AppendLine("paramType: String!");
            sb.AppendLine("dbType: String!");
            sb.AppendLine("isNullable: Boolean!");
            sb.AppendLine("isReadOnly: Boolean!");
            sb.AppendLine("isPrimaryKey: Boolean!");
            sb.AppendLine("isIdentity: Boolean!");
            sb.AppendLine("isCreatedOnColumn: Boolean!");
            sb.AppendLine("isCreatedByColumn: Boolean!");
            sb.AppendLine("isUpdatedOnColumn: Boolean!");
            sb.AppendLine("isUpdatedByColumn: Boolean!");
            sb.AppendLine("isDeletedOnColumn: Boolean!");
            sb.AppendLine("isDeletedColumn: Boolean!");
            sb.AppendLine("metadata: [dbMetadataSchema!]!");
            sb.AppendLine("}");

            sb.AppendLine("type dbMetadataSchema { key: String! value: String! }");

            sb.AppendLine("enum AggregateOperations {");
            sb.AppendLine(string.Join(',', Enum.GetNames(typeof(AggregateOperationType))));
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static StringBuilder GetInputAndArgumentTypes(IDbModel model, List<TableSchemaGenerator> tableGenerators, List<StoredProcedureSchemaGenerator> mutatingSpGenerators)
        {
            var builder = new StringBuilder();
            builder.AppendLine("type databaseInput {");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetInputFieldDefinition());
            }
            foreach (var generator in mutatingSpGenerators)
            {
                builder.AppendLine(generator.GetFieldDefinition());
            }

            builder.AppendLine("}");

            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Insert, IdentityType.None));
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Update, IdentityType.Required));
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Upsert, IdentityType.Optional));
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Delete, IdentityType.Optional, true));
                builder.AppendLine(generator.GetBatchMutationParameterType());

                builder.AppendLine(generator.GetTableFilterDefinition());

                builder.AppendLine(generator.GetJoinDefinitions(model));

                builder.AppendLine(generator.GetTableJoinType());
                builder.AppendLine(generator.GetAggregateLinkDefinitions());

                builder.AppendLine(generator.GetTableColumnEnumDefinition());
                builder.AppendLine(generator.GetTableSortEnumDefinition());
            }

            return builder;
        }

        public static string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
        {
            if (dataType == "datetime2" || dataType == "datetime" || dataType == "datetimeoffset")
                return $"String{(isNullable ? "" : "!")}";

            return $"{GetSimpleGraphQlTypeName(dataType)}{(isNullable ? "" : "!")}";
        }

        public static string GetGraphQlTypeName(string dataType, bool isNullable = false)
        {
            return $"{GetSimpleGraphQlTypeName(dataType)}{(isNullable ? "" : "!")}";
        }

        public static string GetFilterInputTypeName(string dataType)
        {
            return $"FilterType{GetSimpleGraphQlTypeName(dataType)}Input";
        }

        private static string GetSimpleGraphQlTypeName(string dataType)
        {
            switch (dataType)
            {
                case "int":
                    return "Int";
                case "smallint":
                    return "Short";
                case "tinyint":
                    return "Byte";
                case "decimal":
                    return "Decimal";
                case "bigint":
                    return "BigInt";
                case "float":
                case "real":
                    return "Float";
                case "datetime":
                case "datetime2":
                    return "DateTime";
                case "datetimeoffset":
                    return "DateTimeOffset";
                case "bit":
                    return "Boolean";
                case "json":
                    return "JSON";
                case "varchar":
                case "nvarchar":
                case "char":
                case "nchar":
                case "binary":
                case "varbinary":
                case "text":
                case "ntext":
                default:
                    return "String";
            }
        }

        internal static string GetFilterType(string gqlType)
        {
            var result = new StringBuilder();
            var name = $"FilterType{gqlType}Input";
            var filters = new (string fieldName, string type)[] {
                ("_eq", gqlType),
                ("_neq", gqlType),
                ("_gt", gqlType),
                ("_gte", gqlType),
                ("_lt", gqlType),
                ("_lte", gqlType),
                ("_in", $"[{gqlType}]"),
                ("_nin", $"[{gqlType}]"),
                ("_between", $"[{gqlType}]"),
                ("_nbetween", $"[{gqlType}]"),
            };
            var stringFilters = new (string fieldName, string type)[] {
                ("_contains", gqlType),
                ("_ncontains", gqlType),
                ("_starts_with", gqlType),
                ("_nstarts_with", gqlType),
                ("_ends_with", gqlType),
                ("_nends_with", gqlType),
                ("_like", gqlType),
                ("_nlike", gqlType),
            };

            result.AppendLine($"input {name} {{");
            foreach (var (fieldName, type) in filters)
            {
                result.AppendLine($"\t{fieldName} : {type}");
            }
            if (gqlType == "String")
            {
                foreach (var (fieldName, type) in stringFilters)
                {
                    result.AppendLine($"\t{fieldName} : {type}");
                }
            }
            result.AppendLine("}");
            return result.ToString();
        }

        internal static bool IsRawSqlEnabled(IDbModel model)
        {
            var value = model.GetMetadataValue("raw-sql");
            return string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsGenericTableEnabled(IDbModel model)
        {
            var value = model.GetMetadataValue(Model.GenericTableConfig.MetadataKey);
            return string.Equals(value, Model.GenericTableConfig.MetadataEnabled, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetGenericTableTypes()
        {
            var sb = new StringBuilder();

            sb.AppendLine("type GenericTableResult {");
            sb.AppendLine("tableName: String!");
            sb.AppendLine("columns: [GenericColumnMetadata!]!");
            sb.AppendLine("rows: [JSON]!");
            sb.AppendLine("totalCount: Int!");
            sb.AppendLine("}");

            sb.AppendLine("type GenericColumnMetadata {");
            sb.AppendLine("name: String!");
            sb.AppendLine("dataType: String!");
            sb.AppendLine("isNullable: Boolean!");
            sb.AppendLine("isPrimaryKey: Boolean!");
            sb.AppendLine("}");

            return sb.ToString();
        }

        public static string GetOnType(IDbTable dbTable)
        {
            var columnEnum = dbTable.ColumnEnumTypeName;
            var result = new StringBuilder();
            var name = dbTable.ColumnFilterTypeName;
            var filters = new (string fieldName, string type)[] {
                ("_eq", columnEnum),
                ("_neq", columnEnum),
                ("_gt", columnEnum),
                ("_gte", columnEnum),
                ("_lt", columnEnum),
                ("_lte", columnEnum),
                ("_in", $"[{columnEnum}]"),
                ("_nin", $"[{columnEnum}]"),
                ("_between", $"[{columnEnum}]"),
                ("_nbetween", $"[{columnEnum}]"),
            };
            var stringFilters = new (string fieldName, string type)[] {
                ("_contains", columnEnum),
                ("_ncontains", columnEnum),
                ("_starts_with", columnEnum),
                ("_nstarts_with", columnEnum),
                ("_ends_with", columnEnum),
                ("_nends_with", columnEnum),
                ("_like", columnEnum),
                ("_nlike", columnEnum),
            };

            result.AppendLine($"input {name} {{");
            foreach (var (fieldName, type) in filters)
            {
                result.AppendLine($"\t{fieldName} : {type}");
            }
            foreach (var (fieldName, type) in stringFilters)
            {
                result.AppendLine($"\t{fieldName} : {type}");
            }
            result.AppendLine("}");
            return result.ToString();
        }
    }
    public enum AggregateOperationType
    {
        None,
        Count,
        Sum,
        Avg,
        Max,
        Min,
    }

}

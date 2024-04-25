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
            builder.AppendLine("schema { query: database mutation: databaseInput }");
            builder.AppendLine("type database {");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetTableFieldDefinition());
            }
            builder.AppendLine("_dbSchema(graphQlName: String): [dbTableSchema!]!");
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

            builder.Append(GetInputAndArgumentTypes(model, tableGenerators));

            //Define the filter types of all the columns in the database, needs to be specific to the connected database, and distinct because of GraphQL.
            foreach (var gqlType in model.Tables.SelectMany(t => t.Columns).Select<ColumnDto, string>(c => GetSimpleGraphQlTypeName(c.DataType)).Distinct())
            {
                builder.AppendLine(GetFilterType(gqlType));
            }

            builder.AppendLine("type dbTableSchema {");
            builder.AppendLine("schema: String!");
            builder.AppendLine("dbName: String!");
            builder.AppendLine("graphQlName: String!");
            builder.AppendLine("primaryKeys: [String!]");
            builder.AppendLine("labelColumn: String!");
            builder.AppendLine("isEditable: Boolean!");
            builder.AppendLine("metadata: [dbMetadataSchema!]!");
            builder.AppendLine("multiJoins: [dbJoinSchema!]!");
            builder.AppendLine("singleJoins: [dbJoinSchema!]!");
            builder.AppendLine("columns: [dbColumnSchema!]!");
            builder.AppendLine("}");

            builder.AppendLine("type dbJoinSchema {");
            builder.AppendLine("name: String!");
            builder.AppendLine("sourceColumnNames: [String!]!");
            builder.AppendLine("destinationTable: String!");
            builder.AppendLine("destinationColumnNames: [String!]!");
            builder.AppendLine("metadata: [dbMetadataSchema!]!");
            builder.AppendLine("}");

            builder.AppendLine("type dbColumnSchema {");
            builder.AppendLine("dbName: String!");
            builder.AppendLine("graphQlName: String!");
            builder.AppendLine("paramType: String!");
            builder.AppendLine("dbType: String!");
            builder.AppendLine("isNullable: Boolean!");
            builder.AppendLine("isReadOnly: Boolean!");
            builder.AppendLine("isPrimaryKey: Boolean!");
            builder.AppendLine("isIdentity: Boolean!");
            builder.AppendLine("isCreatedOnColumn: Boolean!");
            builder.AppendLine("isCreatedByColumn: Boolean!");
            builder.AppendLine("isUpdatedOnColumn: Boolean!");
            builder.AppendLine("isUpdatedByColumn: Boolean!");
            builder.AppendLine("isDeletedOnColumn: Boolean!");
            builder.AppendLine("isDeletedColumn: Boolean!");
            builder.AppendLine("metadata: [dbMetadataSchema!]!");
            builder.AppendLine("}");
            
            builder.AppendLine("type dbMetadataSchema { key: String! value: String! }");

            builder.AppendLine("enum AggregateOperations {");
            builder.AppendLine( string.Join(',', Enum.GetNames(typeof (AggregateOperationType))));
            builder.AppendLine("}");

            return builder.ToString();

        }

        private static StringBuilder GetInputAndArgumentTypes(IDbModel model, List<TableSchemaGenerator> tableGenerators)
        {
            var builder = new StringBuilder();
            builder.AppendLine("type databaseInput {");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetInputFieldDefinition());
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

        private static string GetFilterType(string gqlType)
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

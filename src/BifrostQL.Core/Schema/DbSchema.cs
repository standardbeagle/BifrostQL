using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using GraphQL.Types;

namespace BifrostQL.Core.Schema
{
    public static class DbSchema
    {
        public static string SchemaTextFromModel(IDbModel model, bool includeDynamicJoins = true)
        {
            var builder = new StringBuilder();
            //builder.AppendLine("directive @len(max: Int!) on FIELD_DEFINITION");
            //builder.AppendLine("directive @identity() on FIELD_DEFINITION");
            //builder.AppendLine("directive @audit() on FIELD_DEFINITION");
            //builder.AppendLine("directive @lookup() on FIELD_DEFINITION");
            //builder.AppendLine("directive @label() on FIELD_DEFINITION");
            //builder.AppendLine("directive @Db(fieldName: String!, ) on FIELD_DEFINITION");
            builder.AppendLine("schema { query: database mutation: databaseInput }");
            builder.AppendLine("type database {");
            foreach (var table in model.Tables)
            {
                builder.AppendLine($"{table.GraphQlName}(limit: Int, offset: Int, sort: [{table.GraphQlName}SortEnum!] filter: TableFilter{table.GraphQlName}Input) : {table.GraphQlName}Paged");
            }
            builder.AppendLine("_dbSchema(graphQlName: String): [dbTableSchema!]!");
            builder.AppendLine("}");

            foreach (var table in model.Tables)
            {
                builder.AppendLine($"type {table.GraphQlName} {{");
                foreach (var column in table.Columns)
                {
                    builder.AppendLine($"\t{column.GraphQlName} : {GetGraphQlTypeName(column.DataType, column.IsNullable)} {GetColumnDirectives(column)}");
                }
                foreach (var link in table.SingleLinks)
                {
                    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.GraphQlName}");
                }
                foreach (var link in table.MultiLinks)
                {
                    builder.AppendLine($"\t{link.Value.ChildTable.GraphQlName}(filter: TableFilter{link.Value.ChildTable.GraphQlName}Input) : [{link.Value.ChildTable.GraphQlName}]");
                }

                if (includeDynamicJoins)
                {
                    foreach (var joinTable in model.Tables)
                    {
                        builder.AppendLine(
                            $"\t_join_{joinTable.GraphQlName}(on: TableOn{table.GraphQlName}{joinTable.GraphQlName}, filter: TableFilter{joinTable.GraphQlName}Input, sort: [{joinTable.GraphQlName}SortEnum!]) : [{joinTable.GraphQlName}!]!");
                        builder.AppendLine(
                            $"\t_single_{joinTable.GraphQlName}(on: [String!]) : {joinTable.GraphQlName}");
                    }
                }

                builder.AppendLine("}");
            }
            foreach (var table in model.Tables)
            {
                builder.AppendLine($"type {table.GraphQlName}Paged {{");
                builder.AppendLine($"\tdata:[{table.GraphQlName}]");
                builder.AppendLine("\ttotal: Int!");
                builder.AppendLine("\toffset: Int");
                builder.AppendLine("\tlimit: Int");
                builder.AppendLine("}");
            }

            builder.AppendLine("type databaseInput {");
            foreach (var table in model.Tables)
            {
                builder.AppendLine($"\t{table.GraphQlName}(insert: Insert{table.GraphQlName}, update: Update{table.GraphQlName}, upsert: Upsert{table.GraphQlName}, delete: Int) : Int");
            }
            builder.AppendLine("}");

            foreach (var table in model.Tables)
            {
                builder.AppendLine(GetInputType("Insert", table, IdentityType.None));
                builder.AppendLine(GetInputType("Update", table, IdentityType.Required));
                builder.AppendLine(GetInputType("Upsert", table, IdentityType.Optional));

                builder.AppendLine($"input TableFilter{table.GraphQlName}Input {{");
                foreach (var column in table.Columns)
                {
                    builder.AppendLine($"\t{column.GraphQlName} : FilterType{GetSimpleGraphQlTypeName(column.DataType)}Input");
                }
                foreach (var link in table.SingleLinks)
                {
                    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : TableFilter{link.Value.ParentTable.GraphQlName}Input");
                }
                builder.AppendLine($"and: [TableFilter{table.GraphQlName}Input!]");
                builder.AppendLine($"or: [TableFilter{table.GraphQlName}Input!]");
                builder.AppendLine("}");
                
                foreach (var joinTable in model.Tables)
                {
                    builder.AppendLine($"input TableOn{table.GraphQlName}{joinTable.GraphQlName} {{");
                    foreach (var column in table.Columns)
                    {
                        builder.AppendLine($"\t{column.GraphQlName} : FilterType{joinTable.GraphQlName}EnumInput");
                    }

                    builder.AppendLine($"and: [TableOn{table.GraphQlName}{joinTable.GraphQlName}!]");
                    builder.AppendLine($"or: [TableOn{table.GraphQlName}{joinTable.GraphQlName}!]");

                    //foreach (var link in table.SingleLinks)
                    //{
                    //    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.GraphQlName}");
                    //}
                    builder.AppendLine("}");
                }

                builder.AppendLine(GetOnType($"{table.GraphQlName}Enum"));
            }

            foreach (var table in model.Tables)
            {
                builder.AppendLine($"enum {table.GraphQlName}Enum {{");
                foreach (var column in table.Columns)
                {
                    builder.AppendLine(column.GraphQlName);
                }
                builder.AppendLine("}");
            }

            foreach (var table in model.Tables)
            {
                builder.AppendLine($"enum {table.GraphQlName}SortEnum {{");
                foreach (var column in table.Columns)
                {
                    builder.AppendLine(column.GraphQlName + "_asc");
                    builder.AppendLine(column.GraphQlName + "_desc");
                }
                builder.AppendLine("}");
            }

            foreach (var gqlType in model.Tables.SelectMany(t => t.Columns).Select(c => GetSimpleGraphQlTypeName(c.DataType)).Distinct())
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
            builder.AppendLine("multiJoins: [dbJoinSchema!]!");
            builder.AppendLine("singleJoins: [dbJoinSchema!]!");
            builder.AppendLine("columns: [dbColumnSchema!]!");
            builder.AppendLine("}");

            builder.AppendLine("type dbJoinSchema {");
            builder.AppendLine("name: String!");
            builder.AppendLine("sourceColumnNames: [String!]!");
            builder.AppendLine("destinationTable: String!");
            builder.AppendLine("destinationColumnNames: [String!]!");
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

            builder.AppendLine("}");
            return builder.ToString();

        }

        private static string GetColumnDirectives(ColumnDto column)
        {
            return "";
        }

        public static ISchema SchemaFromModel(IDbModel model, bool includeDynamicJoins)
        {
            var schemaText = SchemaTextFromModel(model, includeDynamicJoins);
            var schema = GraphQL.Types.Schema.For(schemaText, _ =>
            {
                var query = _.Types.For("database");
                var mut = _.Types.For("databaseInput");
                foreach (var table in model.Tables)
                {
                    var tableField = query.FieldFor(table.GraphQlName);
                    tableField.Resolver = new DbTableResolver();
                    var tableType = _.Types.For(table.GraphQlName);

                    var tableInsertField = mut.FieldFor(table.GraphQlName);
                    tableInsertField.Resolver = new DbTableMutateResolver();

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
                        var joinField = tableType.FieldFor($"_join_{joinTable.GraphQlName}");
                        joinField.Resolver = DbJoinFieldResolver.Instance;
                        var singleField = tableType.FieldFor($"_single_{joinTable.GraphQlName}");
                        singleField.Resolver = DbJoinFieldResolver.Instance;
                    }
                }

                var dbSchema = query.FieldFor("_dbSchema");
                dbSchema.Resolver = new DbSchemaResolver(model);

            });
            return schema;
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
        public static string GetSimpleGraphQlTypeName(string dataType)
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

        public static string GetFilterType(string gqlType)
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
        public static string GetOnType(string columnEnum)
        {
            var result = new StringBuilder();
            var name = $"FilterType{columnEnum}Input";
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


        public static string GetInputType(string action, TableDto table, IdentityType identityType)
        {
            var result = new StringBuilder();
            var name = action + table.GraphQlName;
            result.AppendLine($"input {name} {{");
            foreach (var column in table.Columns)
            {
                if (identityType == IdentityType.None && column.IsIdentity)
                    continue;

                var isNullable = column.IsNullable;
                if (column.IsCreatedOnColumn || column.IsCreatedByColumn || column.IsUpdatedByColumn || column.IsUpdatedOnColumn)
                    isNullable = true;
                if (identityType == IdentityType.Optional && column.IsIdentity)
                    isNullable = true;
                if (identityType == IdentityType.Required && column.IsIdentity)
                    isNullable = false;

                result.AppendLine($"\t{column.GraphQlName} : {GetGraphQlInsertTypeName(column.DataType, isNullable)}");
            }
            result.AppendLine("}");
            return result.ToString();
        }
    }
    public enum IdentityType
    {
        None,
        Optional,
        Required
    }
}
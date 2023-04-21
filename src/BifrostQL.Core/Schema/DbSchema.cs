using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using GraphQL.Types;

namespace BifrostQL.Core.Schema
{
    public static class DbSchema
    {
        public static string SchemaTextFromModel(IDbModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("schema { query: database mutation: databaseInput }");
            builder.AppendLine("type database {");
            foreach (var table in model.Tables)
            {
                builder.AppendLine($"{table.GraphQlName}(limit: Int, offset: Int, sort: [String!] filter: TableFilter{table.GraphQlName}Input) : {table.GraphQlName}Paged");
            }
            builder.AppendLine("}");

            foreach (var table in model.Tables)
            {
                builder.AppendLine($"type {table.GraphQlName} {{");
                foreach (var column in table.Columns)
                {
                    builder.AppendLine($"\t{column.GraphQlName} : {GetGraphQlTypeName(column.DataType, column.IsNullable)}");
                }
                foreach (var link in table.SingleLinks)
                {
                    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.GraphQlName}");
                }
                foreach (var link in table.MultiLinks)
                {
                    builder.AppendLine($"\t{link.Value.ChildTable.GraphQlName}(filter: TableFilter{link.Value.ChildTable.GraphQlName}Input) : [{link.Value.ChildTable.GraphQlName}]");
                }
                foreach (var joinTable in model.Tables)
                {
                    builder.AppendLine($"\t_join_{joinTable.GraphQlName}(on: [String!], filter: TableFilter{joinTable.GraphQlName}Input, sort: [String!]) : [{joinTable.GraphQlName}!]!");
                    builder.AppendLine($"\t_single_{joinTable.GraphQlName}(on: [String!]) : {joinTable.GraphQlName}");
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
                builder.AppendLine($"and: [TableFilter{table.GraphQlName}Input]");
                builder.AppendLine($"or: [TableFilter{table.GraphQlName}Input]");
                builder.AppendLine("}");
            }

            foreach (var gqlType in model.Tables.SelectMany(t => t.Columns).Select(c => GetSimpleGraphQlTypeName(c.DataType)).Distinct())
            {
                builder.AppendLine(GetFilterType(gqlType));
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

            return builder.ToString();

        }

        public static ISchema SchemaFromModel(IDbModel model)
        {
            var schemaText = SchemaTextFromModel(model);
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
            });
            return schema;
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

                result.AppendLine($"\t{column.GraphQlName} : {GetGraphQlTypeName(column.DataType, isNullable)}");
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
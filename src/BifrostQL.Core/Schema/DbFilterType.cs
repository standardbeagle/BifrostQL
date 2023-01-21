using GraphQL.Types;

namespace BifrostQL.Schema
{
    public class DbFilterType : InputObjectGraphType
    {
        public DbFilterType(string dataType)
        {
            Name = $"FilterType{GraphTypeFromSql(dataType).GetType().Name}";
            var filters = new (string fieldName, IGraphType type)[] {
                            ("_eq", GraphTypeFromSql(dataType)),
                            ("_neq", GraphTypeFromSql(dataType)),
                            ("_gt", GraphTypeFromSql(dataType)),
                            ("_gte", GraphTypeFromSql(dataType)),
                            ("_lt", GraphTypeFromSql(dataType)),
                            ("_lte", GraphTypeFromSql(dataType)),
                            ("_contains", GraphTypeFromSql(dataType)),
                            ("_ncontains", GraphTypeFromSql(dataType)),
                            ("_starts_with", GraphTypeFromSql(dataType)),
                            ("_nstarts_with", GraphTypeFromSql(dataType)),
                            ("_ends_with", GraphTypeFromSql(dataType)),
                            ("_nends_with", GraphTypeFromSql(dataType)),
                            ("_in", new ListGraphType(GraphTypeFromSql(dataType))),
                            ("_nin", new ListGraphType(GraphTypeFromSql(dataType))),
                            ("_between", new ListGraphType(GraphTypeFromSql(dataType))),
                            ("_nbetween", new ListGraphType(GraphTypeFromSql(dataType))),
                        };
            foreach (var (fieldName, type) in filters)
            {
                AddField(new FieldType
                {
                    Name = fieldName,
                    ResolvedType = type
                });
            }
        }
        public IGraphType GraphTypeFromSql(string sqlType)
        {
            switch (sqlType)
            {
                case "float":
                case "real":
                    return new FloatGraphType();
                case "datetimeOffset":
                    return new DateTimeOffsetGraphType();
                case "datetime":
                case "datetime2":
                    return new DateTimeGraphType();
                case "bit":
                    return new BooleanGraphType();
                case "int":
                case "smallint":
                case "tinyint":
                case "money":
                case "decimal":
                    return new IntGraphType();
                case "image":
                    return new StringGraphType();
                case "varchar":
                case "nvarchar":
                case "char":
                case "nchar":
                case "binary":
                case "varbinary":
                case "ntext":
                case "text":
                    return new StringGraphType();
                default:
                    return new ObjectGraphType();
            }
        }

        public static string GetSingleFilter(string? table, string field, string op, object? value)
        {
            var rel = op switch
            {
                "_eq" => "=",
                "_neq" => "!=",
                "_lt" => "<",
                "_lte" => "<=",
                "_gt" => ">",
                "_gte" => ">=",
                "_contains" or "_starts_with" or "_ends_with" => "like",
                "_ncontains" or "_nstarts_with" or "_nends_with" => "not like",
                "_in" => "in",
                "_nin" => "not in",
                "_between" => "between",
                "_nbetween" => "not between",
                _ => "="
            };
            var val = op switch
            {
                "_starts_with" or "_nstarts_with" => $"'{value}%'",
                "_ends_with" or "_nends_with" => $"'%{value}'",
                "_contains" or "_ncontains" => $"'%{value}%'",
                "_in" or "_nin" => $"('{string.Join("','", (object[])(value ?? Array.Empty<object>()))}')",
                "_between" or "_nbetween" => $"'{string.Join("' AND '", (object[])(value ?? Array.Empty<object>()))}'",
                _ => $"'{value}'"
            };
            if (op == "_eq" && val == null)
            {
                rel = "IS NULL";
                val = "";
            }
            if (op == "_neq" && val == null)
            {
                rel = "IS NOT NULL";
                val = "";
            }
            if (table == null)
            {
                string filter = $"[{field}] {rel} {val}";
                return filter;
            }
            return $"[{table}].[{field}] {rel} {val}";
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Pure translation layer between MCP tool arguments (JSON) and the
    /// programmatic query model (<see cref="GqlObjectQuery"/> /
    /// <see cref="TableFilter"/>). Nothing here touches SQL or GraphQL text —
    /// filters compile into the same <see cref="TableFilter"/> tree the GraphQL
    /// pipeline builds, so values always bind as SQL parameters through the
    /// existing machinery. Every validation failure throws
    /// <see cref="ToolPromptException"/> with an agent-actionable message.
    /// </summary>
    internal static partial class QueryToolCompiler
    {
        /// <summary>Full existing filter-operator vocabulary; the compiler maps, never invents.</summary>
        private static readonly string[] SupportedOperators =
        {
            FilterOperators.Eq, FilterOperators.Neq,
            FilterOperators.Lt, FilterOperators.Lte, FilterOperators.Gt, FilterOperators.Gte,
            FilterOperators.Contains, FilterOperators.NContains,
            FilterOperators.StartsWith, FilterOperators.NStartsWith,
            FilterOperators.EndsWith, FilterOperators.NEndsWith,
            FilterOperators.Like, FilterOperators.NLike,
            FilterOperators.In, FilterOperators.NIn,
            FilterOperators.Between, FilterOperators.NBetween,
            FilterOperators.Null,
        };

        private static readonly HashSet<string> ListOperators = new()
        {
            FilterOperators.In, FilterOperators.NIn, FilterOperators.Between, FilterOperators.NBetween,
        };

        /// <summary>
        /// Compiles a structured filter object into a <see cref="TableFilter"/>.
        /// Shape: <c>{column: {_op: value}, ...}</c> — sibling keys AND together —
        /// plus explicit <c>{"and": [...]}</c> / <c>{"or": [...]}</c> groups, which
        /// <see cref="TableFilter"/> supports natively (OR over plain columns; OR
        /// over relationship filters is rejected downstream by the existing
        /// machinery rather than silently altered here).
        /// </summary>
        public static TableFilter CompileFilter(IDbTable table, JsonElement filter)
            => TableFilter.FromObject(CompileFilterObject(table, filter), table.DbName);

        private static Dictionary<string, object?> CompileFilterObject(IDbTable table, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ToolPromptException(
                    "filter must be a JSON object mapping columns to operator objects, " +
                    "e.g. {\"status\":{\"_eq\":\"open\"}} or {\"or\":[{\"a\":{\"_eq\":1}},{\"b\":{\"_null\":true}}]}.");

            var result = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "and" or "or")
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new ToolPromptException(
                            $"'{property.Name}' must be an array of filter objects, " +
                            $"e.g. {{\"{property.Name}\":[{{\"status\":{{\"_eq\":\"open\"}}}},{{\"name\":{{\"_contains\":\"acme\"}}}}]}}.");
                    result[property.Name] = property.Value.EnumerateArray()
                        .Select(item => (object)CompileFilterObject(table, item))
                        .ToList();
                    continue;
                }

                var column = ResolveColumn(table, property.Name);
                result[column.GraphQlName] = CompileOperatorObject(column, property.Value);
            }

            if (result.Count == 0)
                throw new ToolPromptException(
                    "filter object is empty. Provide at least one column condition, e.g. {\"status\":{\"_eq\":\"open\"}}, or omit filter.");
            return result;
        }

        private static Dictionary<string, object?> CompileOperatorObject(ColumnDto column, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new ToolPromptException(
                    $"Filter for column '{column.ColumnName}' must be an operator object, " +
                    $"e.g. {{\"{column.ColumnName}\":{{\"_eq\":...}}}}, not a bare value.");

            var ops = new Dictionary<string, object?>();
            foreach (var property in value.EnumerateObject())
            {
                var op = property.Name;
                if (!SupportedOperators.Contains(op, StringComparer.Ordinal))
                    throw new ToolPromptException(
                        $"Unknown filter operator '{op}' on column '{column.ColumnName}'. " +
                        $"Supported operators: {string.Join(", ", SupportedOperators)}. " +
                        $"Example: {{\"{column.ColumnName}\":{{\"_eq\":\"value\"}}}} or {{\"{column.ColumnName}\":{{\"_in\":[1,2,3]}}}}.");

                var clrValue = ToClrValue(property.Value);
                if (ListOperators.Contains(op) && clrValue is not IEnumerable<object?>)
                    throw new ToolPromptException(
                        $"Operator '{op}' requires an array value, e.g. " +
                        (op is FilterOperators.Between or FilterOperators.NBetween
                            ? $"{{\"{column.ColumnName}\":{{\"{op}\":[low,high]}}}} (exactly two bounds)."
                            : $"{{\"{column.ColumnName}\":{{\"{op}\":[v1,v2]}}}}."));
                ops[op] = clrValue;
            }

            if (ops.Count == 0)
                throw new ToolPromptException(
                    $"Filter for column '{column.ColumnName}' has no operator. " +
                    $"Example: {{\"{column.ColumnName}\":{{\"_eq\":\"value\"}}}}.");
            return ops;
        }

        /// <summary>
        /// Resolves a caller-supplied column name against both name spaces
        /// (GraphQL and raw DB), matching the tolerance of the filter machinery.
        /// </summary>
        public static ColumnDto ResolveColumn(IDbTable table, string name)
        {
            if (table.GraphQlLookup.TryGetValue(name, out var byGraphQl))
                return byGraphQl;
            if (table.ColumnLookup.TryGetValue(name, out var byDb))
                return byDb;
            throw new ToolPromptException(SchemaDescriber.UnknownColumnMessage(table, name));
        }

        /// <summary>
        /// Validates sort tokens and normalizes their column part to the GraphQL
        /// name the SQL sort renderer keys on.
        /// </summary>
        public static List<string> CompileSort(IDbTable table, IReadOnlyList<string> tokens)
        {
            var compiled = new List<string>(tokens.Count);
            foreach (var token in tokens)
            {
                var (columnPart, suffix) = token switch
                {
                    _ when token.EndsWith("_asc", StringComparison.Ordinal) => (token[..^4], "_asc"),
                    _ when token.EndsWith("_desc", StringComparison.Ordinal) => (token[..^5], "_desc"),
                    _ => throw new ToolPromptException(
                        $"Invalid sort token '{token}'. Use '<column>_asc' or '<column>_desc', e.g. 'name_asc'."),
                };
                var column = ResolveColumn(table, columnPart);
                compiled.Add(column.GraphQlName + suffix);
            }
            return compiled;
        }

        /// <summary>
        /// Default deterministic sort — primary key ascending — so cursor paging
        /// yields stable pages even when the caller supplies no sort. Empty for
        /// keyless tables (views), where the caller must sort explicitly for
        /// stable pages.
        /// </summary>
        public static List<string> DefaultSort(IDbTable table) =>
            table.KeyColumns.Select(c => c.GraphQlName + "_asc").ToList();

        /// <summary>
        /// Column selection for <c>detail=summary</c>: primary key + display
        /// column + short bounded string columns. "Short" is judged from a
        /// declared length in the type name (e.g. <c>nvarchar(50)</c>) because
        /// column metadata carries no separate length field; providers that
        /// report bare type names (SQL Server INFORMATION_SCHEMA, SQLite TEXT)
        /// therefore contribute only key + display columns — a documented
        /// limitation, not a bug.
        /// </summary>
        public static List<ColumnDto> SummaryColumns(IDbTable table)
        {
            const int shortStringMaxLength = 64;
            var columns = new List<ColumnDto>(table.KeyColumns);
            if (SchemaDescriber.DisplayColumn(table) is { } display && !columns.Contains(display))
                columns.Add(display);
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
            {
                if (columns.Contains(column) || !SchemaDescriber.IsStringType(column.DataType))
                    continue;
                var lengthMatch = DeclaredLengthRegex().Match(column.DataType);
                if (lengthMatch.Success && int.Parse(lengthMatch.Groups[1].Value) <= shortStringMaxLength)
                    columns.Add(column);
            }
            return columns;
        }

        [GeneratedRegex(@"\((\d+)\)")]
        private static partial Regex DeclaredLengthRegex();

        /// <summary>Converts a JSON argument value into the CLR shapes <see cref="TableFilter"/> binds as SQL parameters.</summary>
        public static object? ToClrValue(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ToClrValue).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ToClrValue(p.Value)),
            _ => throw new ToolPromptException($"Unsupported JSON value kind '{element.ValueKind}' in arguments."),
        };

        /// <summary>
        /// Coerces one primary-key value to the CLR type its column compares
        /// against (SQLite in particular will not equate the string '1' with the
        /// integer 1 through a parameter). String→number parsing failures become
        /// prompt errors naming the column and its type.
        /// </summary>
        public static object? CoerceKeyValue(ColumnDto column, object? value)
        {
            if (value is not string s)
                return value;
            var type = column.DataType.ToLowerInvariant();
            var isInteger = type.Contains("int");
            var isDecimal = !isInteger && (type.Contains("decimal") || type.Contains("numeric")
                || type.Contains("real") || type.Contains("float") || type.Contains("double") || type.Contains("money"));
            if (!isInteger && !isDecimal)
                return value;
            if (isInteger && long.TryParse(s, out var l))
                return l;
            if (isDecimal && decimal.TryParse(s, out var d))
                return d;
            throw new ToolPromptException(
                $"id value '{s}' is not valid for primary-key column '{column.ColumnName}' ({column.DataType}).");
        }

        /// <summary>
        /// Builds a root-level programmatic query for <paramref name="table"/>
        /// projecting <paramref name="columns"/> by their DB names (result rows
        /// are keyed by the same DB column names the schema tools expose).
        /// </summary>
        public static GqlObjectQuery BuildQuery(IDbTable table, IEnumerable<ColumnDto> columns)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in columns)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            return query;
        }
    }
}

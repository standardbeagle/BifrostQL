using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using ModelContextProtocol.Protocol;
using static BifrostQL.Mcp.ToolJson;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// The <c>bifrost_aggregate</c> MCP tool: GROUP BY aggregation over one
    /// table, compiled onto the existing grouped-aggregate query machinery
    /// (<see cref="GroupedAggregate"/>) and executed through
    /// <see cref="IQueryIntentExecutor"/> — so tenant isolation and soft-delete
    /// filters constrain the rows BEFORE grouping, exactly like the GraphQL
    /// aggregate surface, and no model input ever reaches SQL text.
    /// </summary>
    internal static class AggregateTools
    {
        internal const string ToolName = "bifrost_aggregate";

        /// <summary>
        /// Steering cap on returned groups: grouped SQL is unpaged, so a
        /// high-cardinality groupBy is truncated here with guidance instead of
        /// flooding the agent's context.
        /// </summary>
        private const int MaxGroups = 100;

        private static readonly string[] MeasureFunctions = { "count", "sum", "avg", "min", "max" };

        private static readonly JsonElement InputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": {
                  "type": "string",
                  "description": "Database table name exactly as listed by bifrost_schema_overview."
                },
                "groupBy": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Columns to group by, in order. Omit for a single whole-table group."
                },
                "measures": {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "properties": {
                      "fn": { "type": "string", "enum": ["count", "sum", "avg", "min", "max"] },
                      "column": { "type": "string", "description": "Numeric column to aggregate. Required for sum/avg/min/max; omit for count (counts rows)." }
                    },
                    "required": ["fn"],
                    "additionalProperties": false
                  },
                  "description": "Aggregate measures, e.g. [{\"fn\":\"count\"},{\"fn\":\"sum\",\"column\":\"total\"}]."
                },
                "filter": {
                  "type": "object",
                  "description": "Same structured filter as bifrost_query: {column: {_op: value}}, applied before grouping. Values always bind as SQL parameters."
                }
              },
              "required": ["table", "measures"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement OutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string" },
                "groups": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "group": { "type": "object", "description": "Group-key values, keyed by groupBy column. Empty object when groupBy was omitted." },
                      "measures": { "type": "object", "description": "Measure values keyed 'count' or '<fn>_<column>'." }
                    },
                    "required": ["group", "measures"]
                  }
                },
                "groupCount": { "type": "integer", "description": "Total groups within your access scope." },
                "returnedCount": { "type": "integer" },
                "message": { "type": "string", "description": "Steering guidance when groups were truncated." }
              },
              "required": ["table", "groups", "groupCount", "returnedCount"]
            }
            """);

        public static Tool ToolDefinition() => new()
        {
            Name = ToolName,
            Title = "Aggregate table rows",
            Description =
                "GROUP BY aggregation over one table: count rows and sum/avg/min/max numeric columns, optionally " +
                "grouped by columns and constrained by the same structured filter as bifrost_query. All reads pass " +
                "through the server's security pipeline — tenant scoping and soft-delete hiding constrain the rows " +
                "before grouping.",
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
        };

        public static async Task<JsonObject> ExecuteAsync(
            IQueryIntentExecutor executor,
            string? endpoint,
            Func<IDictionary<string, object?>> userContextProvider,
            CallToolRequestParams parameters,
            CancellationToken cancellationToken)
        {
            var args = parameters.Arguments;
            var tableName = GetStringArgument(args, "table")
                ?? throw new ToolPromptException(
                    "Missing required argument 'table'. Call bifrost_schema_overview to list the available tables.");

            var model = await executor.GetModelAsync(endpoint);
            var table = ResolveTable(model, tableName);

            var groupColumns = CompileGroupColumns(table, GetStringArray(args, "groupBy"));
            var (includeCount, valueColumns, measureKeys) = CompileMeasures(model, table, GetArgument(args, "measures"));

            var query = QueryToolCompiler.BuildQuery(table, Enumerable.Empty<ColumnDto>());
            query.GroupedAggregate = new GroupedAggregate
            {
                GroupColumns = groupColumns,
                IncludeCount = includeCount,
                ValueColumns = valueColumns,
            };
            if (GetArgument(args, "filter") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } filterElement)
                query.Filter = QueryToolCompiler.CompileFilter(table, filterElement);

            var result = await executor.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = new Dictionary<string, object?>(userContextProvider()),
                Endpoint = endpoint,
            }, cancellationToken);

            var groups = new JsonArray();
            foreach (var row in result.Rows.Take(MaxGroups))
            {
                var group = new JsonObject();
                foreach (var groupColumn in groupColumns)
                    group[groupColumn.GraphQlName] = ToJsonNode(row.GetValueOrDefault(groupColumn.GraphQlName));
                var measures = new JsonObject();
                foreach (var (key, alias) in measureKeys)
                    measures[key] = ToJsonNode(row.GetValueOrDefault(alias));
                groups.Add(new JsonObject { ["group"] = group, ["measures"] = measures });
            }

            var payload = new JsonObject
            {
                ["table"] = table.DbName,
                ["groups"] = groups,
                ["groupCount"] = result.Rows.Count,
                ["returnedCount"] = groups.Count,
            };
            if (result.Rows.Count > MaxGroups)
            {
                payload["message"] =
                    $"{result.Rows.Count} groups match; showing the first {MaxGroups} — " +
                    "add a filter or group by fewer / lower-cardinality columns.";
            }
            return payload;
        }

        private static IReadOnlyList<AggregateGroupColumn> CompileGroupColumns(
            IDbTable table, IReadOnlyList<string>? groupBy)
        {
            if (groupBy is null)
                return Array.Empty<AggregateGroupColumn>();
            if (groupBy.Count == 0)
                throw new ToolPromptException("groupBy must be a non-empty array of column names, or be omitted.");

            var result = new List<AggregateGroupColumn>(groupBy.Count);
            foreach (var name in groupBy)
            {
                var column = QueryToolCompiler.ResolveColumn(table, name);
                if (result.All(g => g.Column.DbName != column.DbName))
                    result.Add(new AggregateGroupColumn(column, column.GraphQlName));
            }
            return result;
        }

        /// <summary>
        /// Compiles the measures array into the grouped-aggregate spec plus the
        /// ordered response keys (<c>count</c> / <c>&lt;fn&gt;_&lt;column&gt;</c>
        /// → SQL alias). Duplicate measures collapse to one so the SQL never
        /// projects the same alias twice.
        /// </summary>
        private static (bool IncludeCount, IReadOnlyList<AggregateValueColumn> ValueColumns,
            IReadOnlyList<(string Key, string Alias)> MeasureKeys) CompileMeasures(
            IDbModel model, IDbTable table, JsonElement? measuresElement)
        {
            if (measuresElement is not { ValueKind: JsonValueKind.Array } measures || measures.GetArrayLength() == 0)
                throw new ToolPromptException(
                    "Missing or empty required argument 'measures'. Provide at least one measure, " +
                    "e.g. [{\"fn\":\"count\"}] or [{\"fn\":\"sum\",\"column\":\"total\"}].");

            var includeCount = false;
            var valueColumns = new List<AggregateValueColumn>();
            var measureKeys = new List<(string Key, string Alias)>();
            foreach (var measure in measures.EnumerateArray())
            {
                if (measure.ValueKind != JsonValueKind.Object)
                    throw new ToolPromptException(
                        "Each measure must be an object {\"fn\":..., \"column\":...}, e.g. {\"fn\":\"sum\",\"column\":\"total\"}.");

                var fn = GetString(measure, "fn")?.ToLowerInvariant()
                    ?? throw new ToolPromptException(
                        $"Measure is missing 'fn'. Supported functions: {string.Join(", ", MeasureFunctions)}.");
                var columnName = GetString(measure, "column");

                if (fn == "count")
                {
                    if (columnName is not null)
                        throw new ToolPromptException(
                            "count counts rows and does not take a column; omit 'column' (use {\"fn\":\"count\"}).");
                    if (!includeCount)
                    {
                        includeCount = true;
                        measureKeys.Add(("count", GroupedAggregate.CountAlias));
                    }
                    continue;
                }

                var operation = fn switch
                {
                    "sum" => AggregateOperationType.Sum,
                    "avg" => AggregateOperationType.Avg,
                    "min" => AggregateOperationType.Min,
                    "max" => AggregateOperationType.Max,
                    _ => throw new ToolPromptException(
                        $"Unknown measure function '{fn}'. Supported functions: {string.Join(", ", MeasureFunctions)}."),
                };
                if (columnName is null)
                    throw new ToolPromptException(
                        $"Measure '{fn}' requires 'column', e.g. {{\"fn\":\"{fn}\",\"column\":\"total\"}}.");

                var column = QueryToolCompiler.ResolveColumn(table, columnName);
                if (!AggregateSurface.IsNumeric(model.TypeMapper.GetGraphQlType(column.EffectiveDataType)))
                {
                    var numeric = AggregateSurface.NumericColumns(table, model.TypeMapper)
                        .Select(c => c.ColumnName).ToArray();
                    throw new ToolPromptException(
                        $"Column '{column.ColumnName}' ({column.DataType}) is not numeric, so '{fn}' cannot aggregate it. " +
                        (numeric.Length > 0
                            ? $"Numeric columns on '{table.DbName}': {string.Join(", ", numeric)}."
                            : $"Table '{table.DbName}' has no numeric columns; only count is available."));
                }

                var opGroup = "_" + fn;
                var alias = AggregateSurface.ValueAlias(opGroup, column.GraphQlName);
                if (valueColumns.All(v => v.SqlAlias != alias))
                {
                    valueColumns.Add(new AggregateValueColumn(operation, column, opGroup, alias));
                    measureKeys.Add(($"{fn}_{column.GraphQlName}", alias));
                }
            }

            return (includeCount, valueColumns, measureKeys);
        }
    }
}

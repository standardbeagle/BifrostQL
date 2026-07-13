using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The built-in read connector: every <c>chat-connector: explore</c> table
    /// becomes one <c>explore_&lt;table&gt;</c> Claude tool whose input schema is
    /// derived from the table's columns (per-column filters with type-appropriate
    /// operators, sort, limit/offset, columns projection — <c>additionalProperties:
    /// false</c> throughout, so the model cannot invent arguments). Execution
    /// re-validates every model-supplied name and operator against the schema —
    /// failures throw <see cref="ChatToolInputException"/> naming the valid choices
    /// so the model can recover — then rides <see cref="IQueryIntentExecutor"/>
    /// under the caller's auth context: tenant isolation, policy row scope, and
    /// encrypted-column decrypt/mask apply by construction, exactly like the chat
    /// store's reads. Results are capped (<see cref="ChatConnectorOptions"/>) and
    /// every trim is reported inside the payload — never silent.
    /// </summary>
    public sealed class ExploreChatConnector : IChatConnector
    {
        /// <summary>
        /// The tool-name prefix, namespacing explore tools against the media_/plan_
        /// tools of the later connector slices.
        /// </summary>
        public const string ToolNamePrefix = "explore_";

        private const string SortAscending = "asc";
        private const string SortDescending = "desc";
        private const string ValidArguments = "filters, sort, limit, offset, columns";

        /// <summary>
        /// Database types the explore schema treats as numeric (range operators)
        /// across the supported dialects: the chat integer-key family plus the
        /// fractional families.
        /// </summary>
        internal static readonly IReadOnlySet<string> NumericColumnTypes =
            new HashSet<string>(ChatConfig.IntegerKeyColumnTypes, StringComparer.OrdinalIgnoreCase)
            {
                "decimal", "numeric", "float", "real", "double", "double precision",
                "money", "smallmoney", "number",
            };

        private enum ColumnFamily { Text, Numeric, Temporal, Other }

        private readonly IQueryIntentExecutor _reads;
        private readonly ChatConnectorOptions _options;
        private readonly string? _endpoint;

        public ExploreChatConnector(
            IQueryIntentExecutor reads, ChatConnectorOptions? options = null, string? endpoint = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _options = options ?? new ChatConnectorOptions();
            _endpoint = endpoint;
        }

        /// <inheritdoc />
        public int Priority => 100;

        /// <summary>The explore tool name for a table: <c>explore_&lt;GraphQL name&gt;</c>.</summary>
        public static string ToolName(IDbTable table) => ToolNamePrefix + table.GraphQlName;

        /// <inheritdoc />
        public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding)
        {
            if (binding is null)
                throw new ArgumentNullException(nameof(binding));
            if (!binding.Config.Explore)
                return Array.Empty<ChatToolDefinition>();

            var table = binding.Table;
            return new[]
            {
                new ChatToolDefinition(
                    ToolName(table),
                    BuildDescription(table, binding.Config),
                    BuildInputSchema(table)),
            };
        }

        /// <inheritdoc />
        public async Task<ChatToolResult> ExecuteAsync(
            string toolName,
            string inputJson,
            IDictionary<string, object?> authContext,
            CancellationToken cancellationToken)
        {
            if (authContext is null)
                throw new ArgumentNullException(nameof(authContext));

            var model = await _reads.GetModelAsync(_endpoint);
            var table = ResolveExploreTable(model, toolName);
            var input = ParseInput(table, inputJson ?? "{}");

            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = BuildQuery(table, input),
                UserContext = authContext,
                Endpoint = _endpoint,
            }, cancellationToken);

            return BuildResult(result.Rows, input.EffectiveLimit);
        }

        // ---- tool definition ------------------------------------------------------

        private static string BuildDescription(IDbTable table, ChatConnectorConfig config)
        {
            var description =
                $"Call this when the user asks about {table.GraphQlName} " +
                $"(table {table.TableSchema}.{table.DbName}). Runs a read-only query and returns " +
                "matching rows as JSON; supports per-column filters, sorting, limit/offset paging, " +
                "and a columns projection.";
            return config.ToolDescription is null ? description : $"{description} {config.ToolDescription}";
        }

        private static string BuildInputSchema(IDbTable table)
        {
            var filterProperties = new JsonObject();
            foreach (var column in table.Columns)
                filterProperties[column.GraphQlName] = ColumnFilterSchema(column);

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["filters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = filterProperties,
                    },
                    ["sort"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["column"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = ColumnNameArray(table),
                            },
                            ["direction"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(SortAscending, SortDescending),
                            },
                        },
                        ["required"] = new JsonArray("column"),
                    },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["columns"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = ColumnNameArray(table),
                        },
                        ["minItems"] = 1,
                    },
                },
            };
            return schema.ToJsonString();
        }

        private static JsonArray ColumnNameArray(IDbTable table) =>
            new(table.Columns.Select(c => (JsonNode)c.GraphQlName).ToArray());

        private static JsonObject ColumnFilterSchema(ColumnDto column)
        {
            var family = Classify(column);
            var properties = new JsonObject();
            foreach (var op in OperatorsFor(family))
            {
                properties[op] = op == FilterOperators.Between
                    ? new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = ValueSchema(family),
                        ["minItems"] = 2,
                        ["maxItems"] = 2,
                    }
                    : ValueSchema(family);
            }
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
            };
        }

        private static ColumnFamily Classify(ColumnDto column)
        {
            var type = StringNormalizer.NormalizeType(column.DataType);
            if (ChatConfig.StringColumnTypes.Contains(type)) return ColumnFamily.Text;
            if (NumericColumnTypes.Contains(type)) return ColumnFamily.Numeric;
            if (ChatConfig.DateTimeColumnTypes.Contains(type)) return ColumnFamily.Temporal;
            return ColumnFamily.Other;
        }

        // Existing operator vocabulary, narrowed per column family: equality for
        // everything, range operators for numeric/temporal, substring for text.
        private static IReadOnlyList<string> OperatorsFor(ColumnFamily family) => family switch
        {
            ColumnFamily.Text => new[] { FilterOperators.Eq, FilterOperators.Contains },
            ColumnFamily.Numeric or ColumnFamily.Temporal => new[]
            {
                FilterOperators.Eq, FilterOperators.Gt, FilterOperators.Gte,
                FilterOperators.Lt, FilterOperators.Lte, FilterOperators.Between,
            },
            _ => new[] { FilterOperators.Eq },
        };

        private static JsonNode ValueSchema(ColumnFamily family) => family switch
        {
            ColumnFamily.Text or ColumnFamily.Temporal => new JsonObject { ["type"] = "string" },
            ColumnFamily.Numeric => new JsonObject { ["type"] = "number" },
            _ => new JsonObject(),
        };

        // ---- input validation (the model is an untrusted caller) --------------------

        private sealed record ExploreInput(
            TableFilter? Filter,
            IReadOnlyList<string> SortTokens,
            IReadOnlyList<ColumnDto> Projection,
            int EffectiveLimit,
            int Offset);

        private static IDbTable ResolveExploreTable(IDbModel model, string toolName)
        {
            foreach (var binding in ChatConnectorConfig.FromModel(model))
            {
                if (binding.Config.Explore && ToolName(binding.Table) == toolName)
                    return binding.Table;
            }
            throw new ChatToolInputException(
                $"Unknown explore tool '{toolName}'; no explore-connector table generates it.");
        }

        private ExploreInput ParseInput(IDbTable table, string inputJson)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(inputJson);
            }
            catch (JsonException ex)
            {
                throw new ChatToolInputException("Tool input must be a JSON object.", ex);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ChatToolInputException("Tool input must be a JSON object.");

                TableFilter? filter = null;
                var sortTokens = new List<string>();
                IReadOnlyList<ColumnDto>? projection = null;
                int? limit = null;
                var offset = 0;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "filters":
                            filter = ParseFilters(table, property.Value);
                            break;
                        case "sort":
                            sortTokens.Add(ParseSort(table, property.Value));
                            break;
                        case "limit":
                            limit = ParseCount(property.Value, "limit", minimum: 1);
                            break;
                        case "offset":
                            offset = ParseCount(property.Value, "offset", minimum: 0);
                            break;
                        case "columns":
                            projection = ParseColumns(table, property.Value);
                            break;
                        default:
                            throw new ChatToolInputException(
                                $"Unknown argument '{property.Name}'. Valid arguments: {ValidArguments}.");
                    }
                }

                // Deterministic paging: the model's sort first, key columns as tiebreak.
                sortTokens.AddRange(table.KeyColumns.Select(c => $"{c.GraphQlName}_{SortAscending}"));

                var effectiveLimit = Math.Min(limit ?? _options.ExploreRowCap, _options.ExploreRowCap);
                return new ExploreInput(
                    filter, sortTokens, projection ?? table.Columns.ToList(), effectiveLimit, offset);
            }
        }

        private static ColumnDto ResolveColumn(IDbTable table, string name, string argument)
        {
            if (table.GraphQlLookup.TryGetValue(name, out var column))
                return column;
            throw new ChatToolInputException(
                $"Unknown column '{name}' in '{argument}' on {table.GraphQlName}. " +
                $"Valid columns: {string.Join(", ", table.Columns.Select(c => c.GraphQlName))}.");
        }

        private static TableFilter? ParseFilters(IDbTable table, JsonElement filters)
        {
            if (filters.ValueKind != JsonValueKind.Object)
                throw new ChatToolInputException(
                    "'filters' must be an object mapping column names to operator objects.");

            var leaves = new List<TableFilter>();
            foreach (var columnProperty in filters.EnumerateObject())
            {
                var column = ResolveColumn(table, columnProperty.Name, "filters");
                if (columnProperty.Value.ValueKind != JsonValueKind.Object)
                    throw new ChatToolInputException(
                        $"The filter for column '{column.GraphQlName}' must be an operator object, " +
                        "e.g. {\"_eq\": value}.");

                var validOperators = OperatorsFor(Classify(column));
                foreach (var operatorProperty in columnProperty.Value.EnumerateObject())
                {
                    if (!validOperators.Contains(operatorProperty.Name, StringComparer.Ordinal))
                        throw new ChatToolInputException(
                            $"Operator '{operatorProperty.Name}' is not valid for column '{column.GraphQlName}'. " +
                            $"Valid operators: {string.Join(", ", validOperators)}.");
                    leaves.Add(FilterLeaf(table, column, operatorProperty.Name, operatorProperty.Value));
                }
            }

            return leaves.Count switch
            {
                0 => null,
                1 => leaves[0],
                _ => new TableFilter { And = leaves, FilterType = FilterType.And },
            };
        }

        private static TableFilter FilterLeaf(IDbTable table, ColumnDto column, string op, JsonElement value) =>
            new()
            {
                TableName = table.DbName,
                ColumnName = column.GraphQlName,
                FilterType = FilterType.Join,
                Next = new TableFilter
                {
                    RelationName = op,
                    Value = FilterValue(column, op, value),
                    FilterType = FilterType.Relation,
                },
            };

        private static object? FilterValue(ColumnDto column, string op, JsonElement value)
        {
            if (op == FilterOperators.Between)
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
                    throw new ChatToolInputException(
                        $"Operator '{FilterOperators.Between}' on column '{column.GraphQlName}' requires " +
                        "an array of exactly two values (lower and upper bound).");
                return value.EnumerateArray().Select(v => ScalarValue(column, op, v)).ToList();
            }

            if (op == FilterOperators.Contains && value.ValueKind != JsonValueKind.String)
                throw new ChatToolInputException(
                    $"Operator '{FilterOperators.Contains}' on column '{column.GraphQlName}' requires a string value.");

            return ScalarValue(column, op, value);
        }

        private static object? ScalarValue(ColumnDto column, string op, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new ChatToolInputException(
                $"Operator '{op}' on column '{column.GraphQlName}' requires a scalar value."),
        };

        private static string ParseSort(IDbTable table, JsonElement sort)
        {
            if (sort.ValueKind != JsonValueKind.Object)
                throw new ChatToolInputException("'sort' must be an object with 'column' and optional 'direction'.");

            string? columnName = null;
            var direction = SortAscending;
            foreach (var property in sort.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "column" when property.Value.ValueKind == JsonValueKind.String:
                        columnName = property.Value.GetString();
                        break;
                    case "direction" when property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is SortAscending or SortDescending:
                        direction = property.Value.GetString()!;
                        break;
                    case "column" or "direction":
                        throw new ChatToolInputException(
                            $"'sort.{property.Name}' must be a string" +
                            $"{(property.Name == "direction" ? $": '{SortAscending}' or '{SortDescending}'" : "")}.");
                    default:
                        throw new ChatToolInputException(
                            $"Unknown sort argument '{property.Name}'. Valid arguments: column, direction.");
                }
            }

            if (columnName is null)
                throw new ChatToolInputException("'sort' requires a 'column'.");

            var column = ResolveColumn(table, columnName, "sort");
            return $"{column.GraphQlName}_{direction}";
        }

        private static IReadOnlyList<ColumnDto> ParseColumns(IDbTable table, JsonElement columns)
        {
            if (columns.ValueKind != JsonValueKind.Array || columns.GetArrayLength() == 0)
                throw new ChatToolInputException("'columns' must be a non-empty array of column names.");

            var projection = new List<ColumnDto>();
            foreach (var element in columns.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw new ChatToolInputException("'columns' must be a non-empty array of column names.");
                projection.Add(ResolveColumn(table, element.GetString()!, "columns"));
            }
            return projection;
        }

        private static int ParseCount(JsonElement value, string name, int minimum)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed >= minimum)
                return parsed;
            throw new ChatToolInputException($"'{name}' must be an integer of at least {minimum}.");
        }

        // ---- query + result -----------------------------------------------------------

        private static GqlObjectQuery BuildQuery(IDbTable table, ExploreInput input)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Filter = input.Filter,
                // One row past the cap proves more rows exist without a count query.
                Limit = input.EffectiveLimit + 1,
                Offset = input.Offset,
            };
            foreach (var column in input.Projection)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName, column.GraphQlName));
            query.Sort.AddRange(input.SortTokens);
            return query;
        }

        private ChatToolResult BuildResult(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int effectiveLimit)
        {
            var hasMore = rows.Count > effectiveLimit;
            var kept = rows.Take(effectiveLimit).ToList();

            var omittedForPayload = 0;
            while (true)
            {
                var payload = new JsonObject
                {
                    ["rows"] = new JsonArray(kept.Select(RowNode).ToArray()),
                };
                var note = BuildNote(hasMore, kept.Count, effectiveLimit + 1, omittedForPayload);
                if (note != null)
                    payload["note"] = note;

                var text = payload.ToJsonString();
                if (text.Length <= _options.ExplorePayloadCharCap || kept.Count == 0)
                    return new ChatToolResult { TextPayload = text };

                kept.RemoveAt(kept.Count - 1);
                omittedForPayload++;
            }
        }

        // Every trim is reported in the payload — a silently truncated result would
        // read as the complete answer.
        private static string? BuildNote(bool hasMore, int shown, int lowerBound, int omittedForPayload)
        {
            var parts = new List<string>();
            if (hasMore)
                parts.Add($"showing {shown} of at least {lowerBound} rows; narrow with filters");
            if (omittedForPayload > 0)
                parts.Add($"{omittedForPayload} rows omitted to fit the payload cap; " +
                    "narrow with filters or select fewer columns");
            return parts.Count == 0 ? null : string.Join(". ", parts);
        }

        private static JsonNode? RowNode(IReadOnlyDictionary<string, object?> row)
        {
            var node = new JsonObject();
            foreach (var (key, value) in row)
                node[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            return node;
        }
    }
}

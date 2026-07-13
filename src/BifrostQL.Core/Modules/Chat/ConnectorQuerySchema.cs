using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The query-shaped tool surface shared by the schema-derived chat connectors
    /// (explore, media): JSON Schema builders for the filter/sort/limit/offset
    /// arguments and the matching validation-first input parsers. One
    /// implementation on purpose — the filter vocabulary the model sees and the
    /// vocabulary execution accepts must be the same code, and every rule here is
    /// security-relevant: <c>visibility: hidden</c> columns are absent from the
    /// whole surface (schema, resolution, and the valid-columns feedback — the
    /// error text goes to the model, so it must not disclose what the schema
    /// hides), and encrypted columns are rejected as predicates (a filter or sort
    /// over ciphertext is a plaintext oracle). Parse failures throw
    /// <see cref="ChatToolInputException"/> naming the valid choices so the model
    /// can recover.
    /// </summary>
    internal static class ConnectorQuerySchema
    {
        internal const string SortAscending = "asc";
        internal const string SortDescending = "desc";

        /// <summary>
        /// Database types the connector schemas treat as numeric (range operators)
        /// across the supported dialects: the chat integer-key family plus the
        /// fractional families.
        /// </summary>
        internal static readonly IReadOnlySet<string> NumericColumnTypes =
            new HashSet<string>(ChatConfig.IntegerKeyColumnTypes, StringComparer.OrdinalIgnoreCase)
            {
                "decimal", "numeric", "float", "real", "double", "double precision",
                "money", "smallmoney", "number",
            };

        internal enum ColumnFamily { Text, Numeric, Temporal, Other }

        /// <summary>
        /// A per-connector veto over filter/sort predicates, invoked after the
        /// shared hidden/encrypted checks (e.g. the media connector rejects
        /// predicates over its media content column). Throw
        /// <see cref="ChatToolInputException"/> to refuse; the message reaches the
        /// model.
        /// </summary>
        internal delegate void PredicateGuard(ColumnDto column, string argument);

        // ---- column surfaces ----------------------------------------------------

        /// <summary>
        /// The columns a connector tool exposes at all: <c>visibility: hidden</c>
        /// columns are omitted from the tool schema, default projections, and the
        /// valid-columns feedback — the same rule every other read surface applies
        /// (TableSchemaGenerator, AggregateSurface, the meta resolvers).
        /// </summary>
        internal static IEnumerable<ColumnDto> VisibleColumns(IDbTable table) =>
            table.Columns.Where(c => !c.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden));

        /// <summary>Visible columns usable as filter/sort predicates: encrypted columns excluded.</summary>
        internal static IEnumerable<ColumnDto> PredicateColumns(IDbTable table) =>
            VisibleColumns(table).Where(c => !IsEncrypted(c));

        internal static bool IsEncrypted(ColumnDto column) =>
            !string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt));

        internal static ColumnFamily Classify(ColumnDto column)
        {
            var type = StringNormalizer.NormalizeType(column.DataType);
            if (ChatConfig.StringColumnTypes.Contains(type)) return ColumnFamily.Text;
            if (NumericColumnTypes.Contains(type)) return ColumnFamily.Numeric;
            if (ChatConfig.DateTimeColumnTypes.Contains(type)) return ColumnFamily.Temporal;
            return ColumnFamily.Other;
        }

        // ---- input schema builders ----------------------------------------------

        internal static JsonArray ColumnNameArray(IEnumerable<ColumnDto> columns) =>
            new(columns.Select(c => (JsonNode)c.GraphQlName).ToArray());

        /// <summary>The <c>filters</c> argument schema over <paramref name="predicateColumns"/>.</summary>
        internal static JsonObject FiltersSchema(IEnumerable<ColumnDto> predicateColumns)
        {
            var filterProperties = new JsonObject();
            foreach (var column in predicateColumns)
                filterProperties[column.GraphQlName] = ColumnFilterSchema(column);
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = filterProperties,
            };
        }

        /// <summary>The <c>sort</c> argument schema over <paramref name="predicateColumns"/>.</summary>
        internal static JsonObject SortSchema(IEnumerable<ColumnDto> predicateColumns) => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["column"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = ColumnNameArray(predicateColumns),
                },
                ["direction"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(SortAscending, SortDescending),
                },
            },
            ["required"] = new JsonArray("column"),
        };

        internal static JsonObject LimitSchema() =>
            new() { ["type"] = "integer", ["minimum"] = 1 };

        internal static JsonObject OffsetSchema() =>
            new() { ["type"] = "integer", ["minimum"] = 0 };

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

        // Existing operator vocabulary, narrowed per column family: equality for
        // everything, range operators for numeric/temporal, substring for text.
        internal static IReadOnlyList<string> OperatorsFor(ColumnFamily family) => family switch
        {
            ColumnFamily.Text => new[] { FilterOperators.Eq, FilterOperators.Contains },
            ColumnFamily.Numeric or ColumnFamily.Temporal => new[]
            {
                FilterOperators.Eq, FilterOperators.Gt, FilterOperators.Gte,
                FilterOperators.Lt, FilterOperators.Lte, FilterOperators.Between,
            },
            _ => new[] { FilterOperators.Eq },
        };

        internal static JsonNode ValueSchema(ColumnFamily family) => family switch
        {
            ColumnFamily.Text or ColumnFamily.Temporal => new JsonObject { ["type"] = "string" },
            ColumnFamily.Numeric => new JsonObject { ["type"] = "number" },
            _ => new JsonObject(),
        };

        // ---- input validation (the model is an untrusted caller) -----------------

        // A hidden column gets the same rejection as a nonexistent one, and the
        // valid-columns feedback lists only visible columns — the error message goes
        // to the model, so it must not disclose what the schema hides.
        internal static ColumnDto ResolveColumn(IDbTable table, string name, string argument)
        {
            if (table.GraphQlLookup.TryGetValue(name, out var column)
                && !column.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
                return column;
            throw new ChatToolInputException(
                $"Unknown column '{name}' in '{argument}' on {table.GraphQlName}. " +
                $"Valid columns: {string.Join(", ", VisibleColumns(table).Select(c => c.GraphQlName))}.");
        }

        // Validation-first encrypted-predicate rejection: without it the read
        // pipeline's guard still refuses the query, but deep in execution as a
        // sanitized server error the model cannot learn from.
        internal static void RejectEncryptedPredicate(ColumnDto column, string argument)
        {
            if (IsEncrypted(column))
                throw new ChatToolInputException(
                    $"Column '{column.GraphQlName}' cannot be used in '{argument}'; it is encrypted.");
        }

        internal static TableFilter? ParseFilters(
            IDbTable table, JsonElement filters, PredicateGuard? predicateGuard = null)
        {
            if (filters.ValueKind != JsonValueKind.Object)
                throw new ChatToolInputException(
                    "'filters' must be an object mapping column names to operator objects.");

            var leaves = new List<TableFilter>();
            foreach (var columnProperty in filters.EnumerateObject())
            {
                var column = ResolveColumn(table, columnProperty.Name, "filters");
                RejectEncryptedPredicate(column, "filters");
                predicateGuard?.Invoke(column, "filters");
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

        internal static TableFilter FilterLeaf(IDbTable table, ColumnDto column, string op, JsonElement value) =>
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

        internal static object? ScalarValue(ColumnDto column, string op, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new ChatToolInputException(
                $"Operator '{op}' on column '{column.GraphQlName}' requires a scalar value."),
        };

        internal static string ParseSort(IDbTable table, JsonElement sort, PredicateGuard? predicateGuard = null)
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
            RejectEncryptedPredicate(column, "sort");
            predicateGuard?.Invoke(column, "sort");
            return $"{column.GraphQlName}_{direction}";
        }

        internal static int ParseCount(JsonElement value, string name, int minimum)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed >= minimum)
                return parsed;
            throw new ChatToolInputException($"'{name}' must be an integer of at least {minimum}.");
        }
    }
}

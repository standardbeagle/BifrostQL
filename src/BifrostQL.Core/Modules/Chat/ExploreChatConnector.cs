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
    /// false</c> throughout, so the model cannot invent arguments).
    /// <c>visibility: hidden</c> columns are excluded from the whole surface, and
    /// encrypted columns from filters/sort (predicates over ciphertext are a
    /// plaintext oracle), matching every other read surface — those rules live in
    /// <see cref="ConnectorQuerySchema"/>, shared with the media connector.
    /// Execution re-validates every model-supplied name and operator against the
    /// schema — failures throw <see cref="ChatToolInputException"/> naming the
    /// valid choices so the model can recover — then rides
    /// <see cref="IQueryIntentExecutor"/> under the caller's auth context: tenant
    /// isolation, policy row scope, and encrypted-column decrypt/mask apply by
    /// construction, exactly like the chat store's reads. Results are capped
    /// (<see cref="ChatConnectorOptions"/>) and every trim is reported inside the
    /// payload — never silent.
    /// </summary>
    public sealed class ExploreChatConnector : IChatConnector
    {
        /// <summary>
        /// The tool-name prefix, namespacing explore tools against the media_/plan_
        /// tools of the other connector slices.
        /// </summary>
        public const string ToolNamePrefix = "explore_";

        private const string ValidArguments = "filters, sort, limit, offset, columns";

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
            // Filters and sort exclude encrypted columns: a predicate over an
            // encrypted column is a plaintext oracle, and the read guard rejects it
            // downstream — the schema must not offer what execution refuses.
            // Projection keeps them (values arrive decrypted or masked).
            var predicateColumns = ConnectorQuerySchema.PredicateColumns(table).ToList();
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["filters"] = ConnectorQuerySchema.FiltersSchema(predicateColumns),
                    ["sort"] = ConnectorQuerySchema.SortSchema(predicateColumns),
                    ["limit"] = ConnectorQuerySchema.LimitSchema(),
                    ["offset"] = ConnectorQuerySchema.OffsetSchema(),
                    ["columns"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = ConnectorQuerySchema.ColumnNameArray(
                                ConnectorQuerySchema.VisibleColumns(table)),
                        },
                        ["minItems"] = 1,
                    },
                },
            };
            return schema.ToJsonString();
        }

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
                            filter = ConnectorQuerySchema.ParseFilters(table, property.Value);
                            break;
                        case "sort":
                            sortTokens.Add(ConnectorQuerySchema.ParseSort(table, property.Value));
                            break;
                        case "limit":
                            limit = ConnectorQuerySchema.ParseCount(property.Value, "limit", minimum: 1);
                            break;
                        case "offset":
                            offset = ConnectorQuerySchema.ParseCount(property.Value, "offset", minimum: 0);
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
                sortTokens.AddRange(table.KeyColumns.Select(
                    c => $"{c.GraphQlName}_{ConnectorQuerySchema.SortAscending}"));

                var effectiveLimit = Math.Min(limit ?? _options.ExploreRowCap, _options.ExploreRowCap);
                return new ExploreInput(
                    filter, sortTokens,
                    projection ?? DefaultProjection(table), effectiveLimit, offset);
            }
        }

        /// <summary>
        /// Visible columns minus the binary family. A blob in every row turned a plain
        /// <c>explore_&lt;table&gt;</c> call into up to ExploreRowCap+1 base64 payloads, almost
        /// all trimmed away again by the payload cap; a model that wants one names it in
        /// <c>columns</c>, and the media connector is the surface for binary content.
        /// </summary>
        internal static IReadOnlyList<ColumnDto> DefaultProjection(IDbTable table) =>
            ConnectorQuerySchema.VisibleColumns(table)
                .Where(c => !ChatConnectorConfig.BinaryColumnTypes.Contains(StringNormalizer.NormalizeType(c.DataType)))
                .ToList();

        private static IReadOnlyList<ColumnDto> ParseColumns(IDbTable table, JsonElement columns)
        {
            if (columns.ValueKind != JsonValueKind.Array || columns.GetArrayLength() == 0)
                throw new ChatToolInputException("'columns' must be a non-empty array of column names.");

            var projection = new List<ColumnDto>();
            foreach (var element in columns.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw new ChatToolInputException("'columns' must be a non-empty array of column names.");
                projection.Add(ConnectorQuerySchema.ResolveColumn(table, element.GetString()!, "columns"));
            }
            return projection;
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
            var nodes = rows.Take(effectiveLimit).Select(RowNode).ToList();
            var (text, _) = ChatRowPayload.Fit(nodes, _options.ExplorePayloadCharCap,
                hasMore, effectiveLimit + 1, "narrow with filters or select fewer columns");
            return new ChatToolResult { TextPayload = text };
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

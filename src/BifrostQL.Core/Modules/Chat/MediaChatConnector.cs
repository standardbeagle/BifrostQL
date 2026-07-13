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

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The built-in media connector: every <c>chat-connector: media</c> table
    /// becomes one <c>media_&lt;table&gt;</c> Claude lookup tool sharing the
    /// explore connector's filter/sort/limit/offset input surface
    /// (<see cref="ConnectorQuerySchema"/> — hidden columns absent, encrypted
    /// columns rejected as predicates, and the media content column itself is
    /// never a predicate). The result shape is FIXED — <c>{id, caption?,
    /// mediaReference}</c> per row, no columns projection — because the tool's
    /// one job is handing out references:
    ///
    /// <list type="bullet">
    /// <item>URL mode (string media column): <c>mediaReference</c> is the stored
    /// URL, used by the client directly.</item>
    /// <item>Binary mode (binary media column): <c>mediaReference</c> is the
    /// opaque <c>bifrost-media://&lt;table&gt;/&lt;id&gt;</c> reference resolved
    /// by the server's auth-gated media fetch endpoint, which re-authorizes the
    /// row through the intent executor on every request — the reference carries
    /// no secret, so it needs no signing and no durable token store.</item>
    /// </list>
    ///
    /// With <c>chat-media-vision: enabled</c> (binary mode only — the validator
    /// rejects it on URL columns) the tool gains one extra input,
    /// <c>view_image_id</c>: the connector loads THAT row's bytes through the
    /// intent read (tenant/policy scope by construction), enforces
    /// <see cref="ChatConnectorOptions.MediaVisionByteCap"/> (over-cap is a
    /// model-visible error, never a silent drop), requires a recognized image
    /// format (<see cref="MediaContentSniffer"/>), and returns the bytes as
    /// <see cref="ChatToolResult.VisionImage"/> for the tool loop to attach as a
    /// base64 vision block. Vision off: the input does not exist in the schema
    /// and bytes never leave the server.
    /// </summary>
    public sealed class MediaChatConnector : IChatConnector
    {
        /// <summary>
        /// The tool-name prefix, namespacing media tools against the explore_/plan_
        /// tools of the other connector slices.
        /// </summary>
        public const string ToolNamePrefix = "media_";

        /// <summary>The URI scheme of binary-mode media references.</summary>
        public const string ReferenceScheme = "bifrost-media";

        /// <summary>The vision input argument: the primary-key value of ONE row to view.</summary>
        public const string ViewImageArgument = "view_image_id";

        private const string LookupArguments = "filters, sort, limit, offset";

        private readonly IQueryIntentExecutor _reads;
        private readonly ChatConnectorOptions _options;
        private readonly string? _endpoint;

        public MediaChatConnector(
            IQueryIntentExecutor reads, ChatConnectorOptions? options = null, string? endpoint = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _options = options ?? new ChatConnectorOptions();
            _endpoint = endpoint;
        }

        /// <inheritdoc />
        public int Priority => 110;

        /// <summary>The media tool name for a table: <c>media_&lt;GraphQL name&gt;</c>.</summary>
        public static string ToolName(IDbTable table) => ToolNamePrefix + table.GraphQlName;

        /// <summary>
        /// The opaque fetch reference for one binary-mode media row, resolved by
        /// <c>GET {chatPath}/media/{table}/{id}</c>.
        /// </summary>
        public static string BuildReference(IDbTable table, object rowId) =>
            $"{ReferenceScheme}://{table.GraphQlName}/{rowId}";

        /// <inheritdoc />
        public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding)
        {
            if (binding is null)
                throw new ArgumentNullException(nameof(binding));
            if (!binding.Config.Media)
                return Array.Empty<ChatToolDefinition>();

            var (table, config) = RequireServableBinding(binding);
            return new[]
            {
                new ChatToolDefinition(
                    ToolName(table),
                    BuildDescription(table, config),
                    BuildInputSchema(table, config)),
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
            var binding = ResolveMediaBinding(model, toolName);
            var (table, config) = RequireServableBinding(binding);
            var input = ParseInput(table, config, inputJson ?? "{}");

            return input.ViewImageId is not null
                ? await ViewImageAsync(table, config, input.ViewImageId, authContext, cancellationToken)
                : await LookupAsync(table, config, input, authContext, cancellationToken);
        }

        // ---- binding rules (fail-fast configuration faults) ------------------------

        // ModelConfigValidator reports all of these at model load; re-asserting here
        // keeps the connector safe against models built without validation and makes
        // the tool-set build the last line of fail-fast.
        private static (IDbTable Table, ChatConnectorConfig Config) RequireServableBinding(
            ChatConnectorBinding binding)
        {
            var table = binding.Table;
            var config = binding.Config;

            if (config.MediaMode is null)
                throw new InvalidOperationException(
                    $"The media connector table {table.TableSchema}.{table.DbName} has no derivable media mode; " +
                    $"'{MetadataKeys.ChatConnector.MediaColumn}' must name a binary-typed or string-typed column.");

            if (config.VisionEnabled && config.MediaMode == ChatMediaMode.Url)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.ChatConnector.MediaVision}' on {table.TableSchema}.{table.DbName} requires a " +
                    "binary-typed media column; a URL-mode column holds no bytes to send as vision input.");

            if (table.KeyColumns.Count() != 1)
                throw new InvalidOperationException(
                    $"The media connector table {table.TableSchema}.{table.DbName} must have exactly one " +
                    "primary-key column; media references name rows by a single id.");

            return (table, config);
        }

        private static ColumnDto KeyColumn(IDbTable table) => table.KeyColumns.Single();

        private static ColumnDto MediaColumn(IDbTable table, ChatConnectorConfig config) =>
            table.ColumnLookup[config.MediaColumn!];

        private static ColumnDto? CaptionColumn(IDbTable table, ChatConnectorConfig config) =>
            config.MediaCaptionColumn is { } caption ? table.ColumnLookup[caption] : null;

        /// <summary>Predicate columns for the tool: the shared rules minus the media content column.</summary>
        private static IEnumerable<ColumnDto> MediaPredicateColumns(IDbTable table, ChatConnectorConfig config) =>
            ConnectorQuerySchema.PredicateColumns(table)
                .Where(c => !string.Equals(c.ColumnName, config.MediaColumn, StringComparison.Ordinal));

        // ---- tool definition ------------------------------------------------------

        private static string BuildDescription(IDbTable table, ChatConnectorConfig config)
        {
            var description =
                $"Call this when the user asks about the media (images/files) in {table.GraphQlName} " +
                $"(table {table.TableSchema}.{table.DbName}). Runs a read-only lookup and returns matching " +
                "rows as JSON, each with a mediaReference the client uses to display the media; supports " +
                "per-column filters, sorting, and limit/offset paging.";
            if (config.VisionEnabled)
                description +=
                    $" To LOOK AT one image yourself, call it again with only '{ViewImageArgument}' set to " +
                    "that row's id; the image is attached to the tool result as vision input.";
            return config.ToolDescription is null ? description : $"{description} {config.ToolDescription}";
        }

        private static string BuildInputSchema(IDbTable table, ChatConnectorConfig config)
        {
            var predicateColumns = MediaPredicateColumns(table, config).ToList();
            var properties = new JsonObject
            {
                ["filters"] = ConnectorQuerySchema.FiltersSchema(predicateColumns),
                ["sort"] = ConnectorQuerySchema.SortSchema(predicateColumns),
                ["limit"] = ConnectorQuerySchema.LimitSchema(),
                ["offset"] = ConnectorQuerySchema.OffsetSchema(),
            };

            // Vision off = the argument does not exist AT ALL: the model cannot even
            // ask for bytes the deployment never sends.
            if (config.VisionEnabled)
                properties[ViewImageArgument] = ConnectorQuerySchema.ValueSchema(
                    ConnectorQuerySchema.Classify(KeyColumn(table)));

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
            };
            return schema.ToJsonString();
        }

        // ---- input validation (the model is an untrusted caller) --------------------

        private sealed record MediaInput(
            TableFilter? Filter,
            IReadOnlyList<string> SortTokens,
            int EffectiveLimit,
            int Offset,
            object? ViewImageId);

        private static ChatConnectorBinding ResolveMediaBinding(IDbModel model, string toolName)
        {
            foreach (var binding in ChatConnectorConfig.FromModel(model))
            {
                if (binding.Config.Media && ToolName(binding.Table) == toolName)
                    return binding;
            }
            throw new ChatToolInputException(
                $"Unknown media tool '{toolName}'; no media-connector table generates it.");
        }

        private MediaInput ParseInput(IDbTable table, ChatConnectorConfig config, string inputJson)
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

                // The media content column is never a predicate: URL strings and blobs
                // are payloads, not query axes.
                void GuardMediaColumn(ColumnDto column, string argument)
                {
                    if (string.Equals(column.ColumnName, config.MediaColumn, StringComparison.Ordinal))
                        throw new ChatToolInputException(
                            $"Column '{column.GraphQlName}' cannot be used in '{argument}'; " +
                            "it is the media content column.");
                }

                TableFilter? filter = null;
                var sortTokens = new List<string>();
                int? limit = null;
                var offset = 0;
                object? viewImageId = null;
                var hasLookupArguments = false;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "filters":
                            filter = ConnectorQuerySchema.ParseFilters(table, property.Value, GuardMediaColumn);
                            hasLookupArguments = true;
                            break;
                        case "sort":
                            sortTokens.Add(ConnectorQuerySchema.ParseSort(table, property.Value, GuardMediaColumn));
                            hasLookupArguments = true;
                            break;
                        case "limit":
                            limit = ConnectorQuerySchema.ParseCount(property.Value, "limit", minimum: 1);
                            hasLookupArguments = true;
                            break;
                        case "offset":
                            offset = ConnectorQuerySchema.ParseCount(property.Value, "offset", minimum: 0);
                            hasLookupArguments = true;
                            break;
                        case ViewImageArgument when config.VisionEnabled:
                            viewImageId = ConnectorQuerySchema.ScalarValue(
                                KeyColumn(table), ViewImageArgument, property.Value);
                            if (viewImageId is null)
                                throw new ChatToolInputException(
                                    $"'{ViewImageArgument}' must be a row id, not null.");
                            break;
                        default:
                            throw new ChatToolInputException(
                                $"Unknown argument '{property.Name}'. Valid arguments: " +
                                $"{(config.VisionEnabled ? $"{LookupArguments}, {ViewImageArgument}" : LookupArguments)}.");
                    }
                }

                if (viewImageId is not null && hasLookupArguments)
                    throw new ChatToolInputException(
                        $"'{ViewImageArgument}' cannot be combined with other arguments; call the tool with " +
                        "only the id of the one image to view.");

                // Deterministic paging: the model's sort first, the key column as tiebreak.
                sortTokens.Add($"{KeyColumn(table).GraphQlName}_{ConnectorQuerySchema.SortAscending}");

                var effectiveLimit = Math.Min(limit ?? _options.ExploreRowCap, _options.ExploreRowCap);
                return new MediaInput(filter, sortTokens, effectiveLimit, offset, viewImageId);
            }
        }

        // ---- lookup ---------------------------------------------------------------

        private async Task<ChatToolResult> LookupAsync(
            IDbTable table,
            ChatConnectorConfig config,
            MediaInput input,
            IDictionary<string, object?> authContext,
            CancellationToken cancellationToken)
        {
            // Binary blobs are never selected by the lookup: the payload carries the
            // opaque reference, and the bytes stay behind the fetch endpoint.
            var projection = new List<ColumnDto> { KeyColumn(table) };
            if (CaptionColumn(table, config) is { } caption)
                projection.Add(caption);
            if (config.MediaMode == ChatMediaMode.Url)
                projection.Add(MediaColumn(table, config));

            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = BuildQuery(table, projection, input.Filter, input.SortTokens,
                    // One row past the cap proves more rows exist without a count query.
                    limit: input.EffectiveLimit + 1, offset: input.Offset),
                UserContext = authContext,
                Endpoint = _endpoint,
            }, cancellationToken);

            var hasMore = result.Rows.Count > input.EffectiveLimit;
            var kept = result.Rows.Take(input.EffectiveLimit).ToList();

            var omittedForPayload = 0;
            while (true)
            {
                var rows = kept.Select(row => MediaRow(table, config, row, contentType: null)).ToList();
                var payload = new JsonObject
                {
                    ["rows"] = new JsonArray(rows.Select(r => (JsonNode)r.Node).ToArray()),
                };
                var note = BuildNote(hasMore, kept.Count, input.EffectiveLimit + 1, omittedForPayload);
                if (note != null)
                    payload["note"] = note;

                var text = payload.ToJsonString();
                if (text.Length <= _options.ExplorePayloadCharCap || kept.Count == 0)
                    return new ChatToolResult
                    {
                        TextPayload = text,
                        MediaReferences = rows
                            .Where(r => r.Reference is not null)
                            .Select(r => r.Reference!)
                            .ToList(),
                    };

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
                parts.Add($"{omittedForPayload} rows omitted to fit the payload cap; narrow with filters");
            return parts.Count == 0 ? null : string.Join(". ", parts);
        }

        /// <summary>
        /// One fixed-shape result row: <c>{id, caption?, contentType?, mediaReference}</c>
        /// plus its stream media reference. A URL-mode row whose stored URL is null
        /// keeps the null <c>mediaReference</c> in the payload (the model sees the row
        /// has no media) but produces no stream reference — there is nothing to fetch.
        /// </summary>
        private static (JsonObject Node, ChatToolMediaReference? Reference) MediaRow(
            IDbTable table,
            ChatConnectorConfig config,
            IReadOnlyDictionary<string, object?> row,
            string? contentType)
        {
            var id = row[KeyColumn(table).GraphQlName]
                ?? throw new InvalidOperationException(
                    $"A {table.TableSchema}.{table.DbName} row returned a null primary key.");

            var node = new JsonObject { ["id"] = JsonSerializer.SerializeToNode(id) };

            string? captionText = null;
            if (CaptionColumn(table, config) is { } caption)
            {
                captionText = row[caption.GraphQlName]?.ToString();
                node["caption"] = captionText;
            }

            if (contentType is not null)
                node["contentType"] = contentType;

            var reference = config.MediaMode == ChatMediaMode.Binary
                ? BuildReference(table, id)
                : row[MediaColumn(table, config).GraphQlName]?.ToString();
            node["mediaReference"] = reference;

            return (node, reference is null
                ? null
                : new ChatToolMediaReference(
                    table.GraphQlName, MediaColumn(table, config).ColumnName, id,
                    contentType, reference, captionText));
        }

        // ---- vision ----------------------------------------------------------------

        private async Task<ChatToolResult> ViewImageAsync(
            IDbTable table,
            ChatConnectorConfig config,
            object viewImageId,
            IDictionary<string, object?> authContext,
            CancellationToken cancellationToken)
        {
            var key = KeyColumn(table);
            var projection = new List<ColumnDto> { key, MediaColumn(table, config) };
            if (CaptionColumn(table, config) is { } caption)
                projection.Add(caption);

            // The intent read applies the caller's row scope: a cross-tenant id and a
            // nonexistent id are the same model-visible "not visible" — fail closed.
            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = BuildQuery(table, projection,
                    filter: PrimaryKeyFilter(table, key, viewImageId),
                    sortTokens: new[] { $"{key.GraphQlName}_{ConnectorQuerySchema.SortAscending}" },
                    limit: 1, offset: 0),
                UserContext = authContext,
                Endpoint = _endpoint,
            }, cancellationToken);

            if (result.Rows.Count == 0)
                throw new ChatToolInputException(
                    $"No {table.GraphQlName} row with {key.GraphQlName} = '{viewImageId}' is visible to you.");

            var row = result.Rows[0];
            var value = row[MediaColumn(table, config).GraphQlName];
            if (value is null)
                throw new ChatToolInputException(
                    $"The {table.GraphQlName} row with {key.GraphQlName} = '{viewImageId}' has no media content.");
            if (value is not byte[] bytes)
                throw new InvalidOperationException(
                    $"The media column '{config.MediaColumn}' on {table.TableSchema}.{table.DbName} returned " +
                    $"'{value.GetType().Name}' instead of bytes; binary-mode media must materialize as byte[].");

            if (bytes.Length > _options.MediaVisionByteCap)
                throw new ChatToolInputException(
                    $"The image is {bytes.Length} bytes, over the {_options.MediaVisionByteCap}-byte vision cap; " +
                    "it cannot be attached as vision input.");

            var mediaType = MediaContentSniffer.SniffImageMediaType(bytes)
                ?? throw new ChatToolInputException(
                    $"The {table.GraphQlName} row with {key.GraphQlName} = '{viewImageId}' is not a recognized " +
                    "image format (png, jpeg, gif, webp); it cannot be attached as vision input.");

            var (node, reference) = MediaRow(table, config, row, mediaType);
            var payload = new JsonObject
            {
                ["rows"] = new JsonArray(node),
                ["note"] = "the image is attached to this tool result as vision input",
            };

            return new ChatToolResult
            {
                TextPayload = payload.ToJsonString(),
                MediaReferences = reference is null
                    ? Array.Empty<ChatToolMediaReference>()
                    : new[] { reference },
                VisionImage = new ChatToolVisionImage(bytes, mediaType),
            };
        }

        private static TableFilter PrimaryKeyFilter(IDbTable table, ColumnDto key, object id) =>
            new()
            {
                TableName = table.DbName,
                ColumnName = key.GraphQlName,
                FilterType = FilterType.Join,
                Next = new TableFilter
                {
                    RelationName = FilterOperators.Eq,
                    Value = id,
                    FilterType = FilterType.Relation,
                },
            };

        // ---- query ------------------------------------------------------------------

        private static GqlObjectQuery BuildQuery(
            IDbTable table,
            IReadOnlyList<ColumnDto> projection,
            TableFilter? filter,
            IReadOnlyList<string> sortTokens,
            int limit,
            int offset)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Filter = filter,
                Limit = limit,
                Offset = offset,
            };
            foreach (var column in projection)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName, column.GraphQlName));
            query.Sort.AddRange(sortTokens);
            return query;
        }
    }
}

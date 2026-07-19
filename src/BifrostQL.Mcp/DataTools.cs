using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using ModelContextProtocol.Protocol;
using static BifrostQL.Mcp.ToolJson;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// The two row-reading MCP tools, both routed exclusively through
    /// <see cref="IQueryIntentExecutor"/> so the security transformer pipeline
    /// (tenant isolation, soft-delete hiding, policy column guards) applies
    /// unconditionally — there is no code path here that renders SQL or GraphQL
    /// text from model input.
    ///
    /// <list type="bullet">
    /// <item><c>bifrost_query</c> — one-table read with structured filter, sort,
    /// field selection, and opaque-cursor pagination (default page 25).</item>
    /// <item><c>bifrost_row_context</c> — one row by primary key plus resolved
    /// parent rows (outgoing FKs) and child-collection summaries (incoming FKs).
    /// Implemented as one intent per relationship rather than a single joined
    /// query: the volume is tiny (one row's neighborhood), each sub-query
    /// independently passes the transformer pipeline, and it avoids re-deriving
    /// the join SQL machinery for a fixed access pattern — a documented
    /// simplicity choice.</item>
    /// </list>
    /// </summary>
    internal static class DataTools
    {
        internal const string QueryToolName = "bifrost_query";
        internal const string RowContextToolName = "bifrost_row_context";

        private const int DefaultPageLimit = 25;
        private const int MaxPageLimit = 200;

        private static readonly JsonElement QueryInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": {
                  "type": "string",
                  "description": "Database table name exactly as listed by bifrost_schema_overview. Required unless page.cursor is set."
                },
                "filter": {
                  "type": "object",
                  "description": "Structured filter: {column: {_op: value}}. Sibling keys AND together; use {\"and\":[...]} / {\"or\":[...]} for explicit groups. Operators: _eq, _neq, _lt, _lte, _gt, _gte, _contains, _in, _between, _null (plus negated/pattern variants _ncontains, _starts_with, _ends_with, _like, _nin, _nbetween). Example: {\"status\":{\"_eq\":\"open\"},\"total\":{\"_between\":[10,100]}}. Values always bind as SQL parameters."
                },
                "fields": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Exact columns to return; overrides detail."
                },
                "sort": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Sort tokens '<column>_asc' / '<column>_desc'. Default: primary key ascending."
                },
                "page": {
                  "type": "object",
                  "properties": {
                    "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 25 },
                    "cursor": { "type": "string", "description": "Opaque nextCursor from a previous call. Carries the whole query; other arguments are ignored (table, if also given, must match)." }
                  },
                  "additionalProperties": false
                },
                "detail": {
                  "type": "string",
                  "enum": ["summary", "full"],
                  "default": "summary",
                  "description": "summary = primary key + display column + short text columns; full = every column."
                }
              },
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement QueryOutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string" },
                "detail": { "type": "string" },
                "rows": { "type": "array", "items": { "type": "object" } },
                "totalCount": { "type": "integer", "description": "Rows matching the filter within your access scope, before paging." },
                "returnedCount": { "type": "integer" },
                "offset": { "type": "integer" },
                "nextCursor": { "type": "string", "description": "Present when more rows match; pass as page.cursor to continue." },
                "message": { "type": "string", "description": "Steering guidance when results were truncated." }
              },
              "required": ["table", "detail", "rows", "totalCount", "returnedCount", "offset"]
            }
            """);

        private static readonly JsonElement RowContextInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string", "description": "Database table name exactly as listed by bifrost_schema_overview." },
                "id": { "description": "Primary-key value. Composite keys: an array in primary-key column order, or a 'v1|v2' delimited string. Never just the first key column." }
              },
              "required": ["table", "id"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement RowContextOutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string" },
                "id": { "type": "array" },
                "row": { "type": "object" },
                "parents": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "relationship": { "type": "string" },
                      "table": { "type": "string" },
                      "id": { "type": "array" },
                      "displayName": { "type": ["string", "null"] },
                      "found": { "type": "boolean", "description": "False when the FK is null or the parent row is outside your access scope." }
                    },
                    "required": ["relationship", "table", "id", "displayName", "found"]
                  }
                },
                "children": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "relationship": { "type": "string" },
                      "table": { "type": "string" },
                      "totalCount": { "type": "integer" },
                      "rows": { "type": "array", "items": { "type": "object" } }
                    },
                    "required": ["relationship", "table", "totalCount", "rows"]
                  }
                }
              },
              "required": ["table", "id", "row", "parents", "children"]
            }
            """);

        public static Tool QueryToolDefinition() => new()
        {
            Name = QueryToolName,
            Title = "Query table rows",
            Description =
                "Read rows from one table with a structured filter, sort, field selection, and cursor pagination " +
                "(default 25 rows per page; follow nextCursor for more). All reads pass through the server's " +
                "security pipeline — tenant scoping, soft-delete hiding, and column policies always apply.",
            InputSchema = QueryInputSchema,
            OutputSchema = QueryOutputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
        };

        public static Tool RowContextToolDefinition() => new()
        {
            Name = RowContextToolName,
            Title = "Row with related context",
            Description =
                "Fetch one row by primary key together with its context in a single call: the row itself, each " +
                "foreign-key parent resolved to its key and display name, and each child collection summarized " +
                "as a total count plus its first rows. Composite primary keys take an array (or 'v1|v2' string) " +
                "in key-column order.",
            InputSchema = RowContextInputSchema,
            OutputSchema = RowContextOutputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
        };

        // ---- bifrost_query ----------------------------------------------------

        public static async Task<JsonObject> ExecuteQueryAsync(
            IQueryIntentExecutor executor,
            string? endpoint,
            Func<IDictionary<string, object?>> userContextProvider,
            CallToolRequestParams parameters,
            CancellationToken cancellationToken)
        {
            var args = parameters.Arguments;
            var page = GetArgument(args, "page");
            var cursorText = page is { } p ? GetString(p, "cursor") : null;

            var model = await executor.GetModelAsync(endpoint);
            ValidatedQuery validated;

            if (cursorText is not null)
            {
                // A cursor is a complete continuation: it snapshots the whole
                // query, so the other arguments are ignored — except a table
                // mismatch, which is a caller error worth failing fast on with a
                // specific message (it concerns the FRESH 'table' argument, not
                // the cursor's own content).
                var cursor = QueryCursor.Decode(cursorText);
                var requestedTable = GetStringArgument(args, "table");
                if (requestedTable is not null
                    && !string.Equals(requestedTable, cursor.Table, StringComparison.OrdinalIgnoreCase))
                    throw new ToolPromptException(
                        $"page.cursor continues a query on table '{cursor.Table}' but table '{requestedTable}' was requested. " +
                        "Drop the cursor to start a new query, or drop the table argument to continue.");

                // The cursor is caller-controlled bytes (unsigned base64 JSON), so
                // EVERY decoded field — table, offset, limit, detail, fields,
                // sort, filter — goes back through the same validation choke
                // point as freshly supplied arguments (ValidateQuery below).
                // Server-issued cursors always pass, so ANY failure here — a
                // malformed filter snapshot, an out-of-range limit, an unknown
                // sort column, a null field entry — means a crafted or corrupted
                // token, and all of them collapse to the one invalid-cursor
                // prompt (no silent clamping, which would mask tampering; e.g.
                // limit=-1 would otherwise hit the dialect's no-limit sentinel
                // and dump the whole table). The broad catch is deliberate: no
                // exception other than ToolPromptException may escape cursor
                // handling, because the transport layer treats anything else as
                // a protocol fault instead of an agent-actionable tool error.
                try
                {
                    var filterElement = cursor.FilterJson is null ? (JsonElement?)null : ParseSchema(cursor.FilterJson);
                    validated = ValidateQuery(
                        model, cursor.Table, cursor.Offset, cursor.Limit, cursor.Detail,
                        cursor.Fields, cursor.Sort, filterElement);
                    // Server-issued cursors carry the table's exact DbName; a
                    // casing variant only arises from tampering.
                    if (!string.Equals(validated.Table.DbName, cursor.Table, StringComparison.Ordinal))
                        throw new ToolPromptException(QueryCursor.InvalidCursorMessage);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new ToolPromptException(QueryCursor.InvalidCursorMessage);
                }
            }
            else
            {
                var tableName = GetStringArgument(args, "table")
                    ?? throw new ToolPromptException(
                        "Missing required argument 'table'. Call bifrost_schema_overview to list the available tables.");
                validated = ValidateQuery(
                    model, tableName,
                    offset: 0,
                    limit: GetPageLimit(page),
                    detail: GetStringArgument(args, "detail") ?? "summary",
                    fields: GetStringArray(args, "fields"),
                    sortTokens: GetStringArray(args, "sort"),
                    filterElement: GetArgument(args, "filter"));
            }

            var result = await executor.ExecuteAsync(new QueryIntent
            {
                Query = validated.Query,
                UserContext = new Dictionary<string, object?>(userContextProvider()),
                Endpoint = endpoint,
            }, cancellationToken);

            var totalCount = result.TotalCount ?? result.Rows.Count;
            var payload = new JsonObject
            {
                ["table"] = validated.Table.DbName,
                ["detail"] = validated.Fields is not null ? "fields" : validated.Detail,
                ["rows"] = ToJsonRows(result.Rows),
                ["totalCount"] = totalCount,
                ["returnedCount"] = result.Rows.Count,
                ["offset"] = validated.Offset,
            };

            if (validated.Offset + result.Rows.Count < totalCount)
            {
                payload["nextCursor"] = new QueryCursor
                {
                    Table = validated.Table.DbName,
                    Offset = validated.Offset + result.Rows.Count,
                    Limit = validated.Limit,
                    Sort = validated.SortTokens,
                    Detail = validated.Detail,
                    Fields = validated.Fields,
                    FilterJson = validated.FilterElement is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } f
                        ? f.GetRawText()
                        : null,
                }.Encode();
                payload["message"] =
                    $"{totalCount} rows match; showing {result.Rows.Count} starting at offset {validated.Offset} — " +
                    "narrow with a filter, e.g. {\"status\":{\"_eq\":\"open\"}}, or pass page.cursor = nextCursor to continue.";
            }

            return payload;
        }

        /// <summary>
        /// The fully validated form of a <c>bifrost_query</c> request: the
        /// resolved table, the compiled query (limit, offset, sort, filter all
        /// applied), and the normalized argument values needed to render the
        /// response payload and the next continuation cursor.
        /// </summary>
        private sealed record ValidatedQuery(
            IDbTable Table,
            GqlObjectQuery Query,
            int Offset,
            int Limit,
            string Detail,
            IReadOnlyList<string>? Fields,
            IReadOnlyList<string> SortTokens,
            JsonElement? FilterElement);

        /// <summary>
        /// Single validation choke point for <c>bifrost_query</c>. Both the
        /// fresh-argument path and the cursor-resume path build their query
        /// exclusively through this method, so a value decoded from a
        /// caller-controlled cursor cannot reach execution with any less
        /// validation than a freshly supplied argument: limit/offset ranges,
        /// the detail enum, non-empty well-formed field and sort lists,
        /// schema resolution of the table, every projected column, and every
        /// sort column, and full filter compilation. Nullable parameter types
        /// are deliberate — cursor JSON can materialize null where fresh
        /// argument parsing never would — and every such hole is rejected here
        /// rather than left to throw NullReference/ArgumentNull downstream.
        /// </summary>
        private static ValidatedQuery ValidateQuery(
            IDbModel model,
            string tableName,
            int offset,
            int limit,
            string? detail,
            IReadOnlyList<string?>? fields,
            IReadOnlyList<string?>? sortTokens,
            JsonElement? filterElement)
        {
            if (limit < 1 || limit > MaxPageLimit)
                throw new ToolPromptException($"page.limit must be an integer between 1 and {MaxPageLimit}.");
            if (offset < 0)
                throw new ToolPromptException("offset must be zero or greater.");
            if (detail is not ("summary" or "full"))
                throw new ToolPromptException($"Invalid detail '{detail}'. Allowed values: summary, full.");
            if (fields is { Count: 0 })
                throw new ToolPromptException("fields must be a non-empty array of column names, or be omitted.");
            if (fields is not null && fields.Any(string.IsNullOrWhiteSpace))
                throw new ToolPromptException("fields must contain only column names.");
            var sort = sortTokens?.Where(t => t is not null).Cast<string>().ToList() ?? new List<string>();
            if (sortTokens is not null && sort.Count != sortTokens.Count)
                throw new ToolPromptException("sort must contain only '<column>_asc' / '<column>_desc' tokens.");

            var table = ResolveTable(model, tableName);
            var fieldNames = fields?.Cast<string>().ToList();
            var columns = fieldNames is not null
                ? fieldNames.Select(f => QueryToolCompiler.ResolveColumn(table, f)).DistinctBy(c => c.ColumnName).ToList()
                : detail == "full"
                    ? table.Columns.OrderBy(c => c.OrdinalPosition).ToList()
                    : QueryToolCompiler.SummaryColumns(table);

            var query = QueryToolCompiler.BuildQuery(table, columns);
            query.Limit = limit;
            query.Offset = offset;
            query.IncludeResult = true;
            query.Sort = sort.Count > 0
                ? QueryToolCompiler.CompileSort(table, sort)
                : QueryToolCompiler.DefaultSort(table);
            if (filterElement is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } fe)
                query.Filter = QueryToolCompiler.CompileFilter(table, fe);

            return new ValidatedQuery(table, query, offset, limit, detail, fieldNames, sort, filterElement);
        }

        private static int GetPageLimit(JsonElement? page)
        {
            if (page is not { } p || p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("limit", out var limitElement))
                return DefaultPageLimit;
            if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetInt32(out var limit))
                throw new ToolPromptException($"page.limit must be an integer between 1 and {MaxPageLimit}.");
            return limit;
        }

        // ---- bifrost_row_context ----------------------------------------------

        public static async Task<JsonObject> ExecuteRowContextAsync(
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
            var idElement = GetArgument(args, "id")
                ?? throw new ToolPromptException("Missing required argument 'id' (the row's primary-key value).");

            var model = await executor.GetModelAsync(endpoint);
            var table = ResolveTable(model, tableName);
            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                throw new ToolPromptException(
                    $"Table '{table.DbName}' has no primary key, so bifrost_row_context cannot address a row. " +
                    "Use bifrost_query with a filter instead.");

            var idValues = ParseIdValues(table, keyColumns, idElement);

            // Dogfooding: the row, its parents, and its child summaries are all
            // compiled by the SAME declarative pipeline that compiles author-declared
            // tools (a per-table definition synthesized from the schema → the
            // declarative compiler → GqlObjectQuery). There is no hand-written query
            // builder here anymore, so the generic tool and declared tools cannot
            // drift. This tool only reshapes the compiled results into its envelope.
            var synthesized = RowContextDefinitionFactory.Build(table);
            var compiled = DeclarativeQueryToolCompiler.Compile(synthesized.Definition, model, executor, endpoint);
            var idArgument = new Dictionary<string, JsonElement>
            {
                [RowContextDefinitionFactory.IdParameterName] = JsonSerializer.SerializeToElement((IReadOnlyList<object?>)idValues),
            };
            var userContext = new Dictionary<string, object?>(userContextProvider());

            var rootResult = await compiled.ExecuteAsync(idArgument, userContext, cancellationToken);
            var row = rootResult.Rows.FirstOrDefault()
                ?? throw new ToolPromptException(
                    $"No row found in '{table.DbName}' with id [{string.Join(", ", idValues)}]. " +
                    "It may not exist, or it may be outside your access scope (tenant and soft-delete rules apply). " +
                    "Use bifrost_query to search for candidate rows.");

            // Parents (outgoing FKs) and child summaries (incoming FKs) both come back
            // as scope-applied collections; each was executed through the intent seam,
            // so an out-of-scope parent reports found=false and child counts carry the
            // caller's tenant/soft-delete scope rather than leaking a wider total.
            var collections = await compiled.ExecuteCollectionIncludesWithCountsAsync(
                idArgument, userContext, cancellationToken);

            var parents = new JsonArray();
            foreach (var parent in synthesized.Parents)
                parents.Add(BuildParentEntry(parent, row, collections));

            var children = new JsonArray();
            foreach (var child in synthesized.Children)
                children.Add(BuildChildEntry(child, collections));

            return new JsonObject
            {
                ["table"] = table.DbName,
                ["id"] = new JsonArray(idValues.Select(ToJsonNode).ToArray()),
                ["row"] = ToJsonRow(row),
                ["parents"] = parents,
                ["children"] = children,
            };
        }

        private static JsonNode BuildParentEntry(
            RowContextDefinitionFactory.ParentRelation parent,
            IReadOnlyDictionary<string, object?> row,
            IReadOnlyDictionary<string, DeclarativeCollectionResult> collections)
        {
            var parentTable = parent.Table;
            // found=false default carries the row's FK values (null when the FK is null),
            // exactly as before; a resolved parent overwrites id with its own key.
            var fkValues = parent.ForeignKeyColumns.Select(c => row.GetValueOrDefault(c.DbName)).ToList();
            var entry = new JsonObject
            {
                ["relationship"] = parent.RelationName,
                ["table"] = parentTable.DbName,
                ["id"] = new JsonArray(fkValues.Select(ToJsonNode).ToArray()),
                ["displayName"] = null,
                ["found"] = false,
            };

            var parentRow = collections.TryGetValue(parent.As, out var result)
                ? result.Rows.FirstOrDefault()
                : null;
            if (parentRow is null)
                return entry; // null FK, or the parent row is outside the caller's scope.

            entry["found"] = true;
            entry["id"] = new JsonArray(parentTable.KeyColumns
                .Select(c => ToJsonNode(parentRow.GetValueOrDefault(c.DbName)))
                .ToArray());
            if (parent.DisplayColumn is not null)
                entry["displayName"] = ToJsonNode(parentRow.GetValueOrDefault(parent.DisplayColumn.DbName));
            return entry;
        }

        private static JsonNode BuildChildEntry(
            RowContextDefinitionFactory.ChildRelation child,
            IReadOnlyDictionary<string, DeclarativeCollectionResult> collections)
        {
            var result = collections.TryGetValue(child.As, out var value)
                ? value
                : DeclarativeCollectionResult.Empty;
            return new JsonObject
            {
                ["relationship"] = child.RelationName,
                ["table"] = child.Table.DbName,
                ["totalCount"] = result.TotalCount ?? result.Rows.Count,
                ["rows"] = ToJsonRows(result.Rows),
            };
        }

        /// <summary>
        /// Parses the <c>id</c> argument into the full ordered primary-key value
        /// list: an array in key order, a 'v1|v2' delimited string, or a single
        /// scalar for single-column keys. Arity mismatches name the key columns
        /// and both accepted forms.
        /// </summary>
        private static List<object?> ParseIdValues(IDbTable table, IReadOnlyList<ColumnDto> keyColumns, JsonElement idElement)
        {
            List<object?> raw = idElement.ValueKind switch
            {
                JsonValueKind.Array => idElement.EnumerateArray().Select(QueryToolCompiler.ToClrValue).ToList(),
                JsonValueKind.String when keyColumns.Count > 1 =>
                    idElement.GetString()!.Split('|').Select(s => (object?)s).ToList(),
                JsonValueKind.String or JsonValueKind.Number => new List<object?> { QueryToolCompiler.ToClrValue(idElement) },
                _ => throw new ToolPromptException(
                    "id must be a primary-key value: a scalar, an array in key-column order, or a 'v1|v2' delimited string."),
            };

            if (raw.Count != keyColumns.Count)
                throw new ToolPromptException(
                    $"Table '{table.DbName}' has a primary key of {keyColumns.Count} column(s) " +
                    $"({string.Join(", ", keyColumns.Select(c => c.ColumnName))}) but id supplied {raw.Count} value(s). " +
                    $"Pass an array in that column order, or a '|'-delimited string.");

            return raw.Select((value, i) => QueryToolCompiler.CoerceKeyValue(keyColumns[i], value)).ToList();
        }

    }
}

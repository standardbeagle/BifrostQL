using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using ModelContextProtocol.Protocol;

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
        private const int ChildSummaryRowCount = 5;

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

            string tableName;
            int offset, limit;
            string detail;
            IReadOnlyList<string>? fields;
            IReadOnlyList<string> sortTokens;
            JsonElement? filterElement;

            if (cursorText is not null)
            {
                // A cursor is a complete continuation: it snapshots the whole
                // query, so the other arguments are ignored — except a table
                // mismatch, which is a caller error worth failing fast on.
                var cursor = QueryCursor.Decode(cursorText);
                var requestedTable = GetStringArgument(args, "table");
                if (requestedTable is not null
                    && !string.Equals(requestedTable, cursor.Table, StringComparison.OrdinalIgnoreCase))
                    throw new ToolPromptException(
                        $"page.cursor continues a query on table '{cursor.Table}' but table '{requestedTable}' was requested. " +
                        "Drop the cursor to start a new query, or drop the table argument to continue.");
                // The cursor is caller-controlled bytes (unsigned base64 JSON), so
                // resumed values pass the same range/enum validation as fresh
                // arguments: limit in [1, MaxPageLimit], offset >= 0, detail in
                // {summary, full}. Server-issued cursors always satisfy these, so
                // any violation means a crafted or corrupted token — rejected with
                // the same prompt as an undecodable cursor (no silent clamping,
                // which would mask tampering; e.g. limit=-1 would otherwise hit
                // the dialect's no-limit sentinel and dump the whole table).
                if (cursor.Limit is < 1 or > MaxPageLimit
                    || cursor.Offset < 0
                    || cursor.Detail is not ("summary" or "full"))
                    throw new ToolPromptException(QueryCursor.InvalidCursorMessage);
                tableName = cursor.Table;
                offset = cursor.Offset;
                limit = cursor.Limit;
                detail = cursor.Detail;
                fields = cursor.Fields;
                sortTokens = cursor.Sort;
                filterElement = cursor.FilterJson is null ? null : ParseSchema(cursor.FilterJson);
            }
            else
            {
                tableName = GetStringArgument(args, "table")
                    ?? throw new ToolPromptException(
                        "Missing required argument 'table'. Call bifrost_schema_overview to list the available tables.");
                offset = 0;
                limit = GetPageLimit(page);
                detail = GetStringArgument(args, "detail") ?? "summary";
                if (detail is not ("summary" or "full"))
                    throw new ToolPromptException($"Invalid detail '{detail}'. Allowed values: summary, full.");
                fields = GetStringArray(args, "fields");
                if (fields is { Count: 0 })
                    throw new ToolPromptException("fields must be a non-empty array of column names, or be omitted.");
                sortTokens = GetStringArray(args, "sort") ?? Array.Empty<string>();
                filterElement = GetArgument(args, "filter");
            }

            var model = await executor.GetModelAsync(endpoint);
            var table = ResolveTable(model, tableName);

            var columns = fields is not null
                ? fields.Select(f => QueryToolCompiler.ResolveColumn(table, f)).DistinctBy(c => c.ColumnName).ToList()
                : detail == "full"
                    ? table.Columns.OrderBy(c => c.OrdinalPosition).ToList()
                    : QueryToolCompiler.SummaryColumns(table);

            var query = QueryToolCompiler.BuildQuery(table, columns);
            query.Limit = limit;
            query.Offset = offset;
            query.IncludeResult = true;
            query.Sort = sortTokens.Count > 0
                ? QueryToolCompiler.CompileSort(table, sortTokens)
                : QueryToolCompiler.DefaultSort(table);
            if (filterElement is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } fe)
                query.Filter = QueryToolCompiler.CompileFilter(table, fe);

            var result = await executor.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = new Dictionary<string, object?>(userContextProvider()),
                Endpoint = endpoint,
            }, cancellationToken);

            var totalCount = result.TotalCount ?? result.Rows.Count;
            var payload = new JsonObject
            {
                ["table"] = table.DbName,
                ["detail"] = fields is not null ? "fields" : detail,
                ["rows"] = ToJsonRows(result.Rows),
                ["totalCount"] = totalCount,
                ["returnedCount"] = result.Rows.Count,
                ["offset"] = offset,
            };

            if (offset + result.Rows.Count < totalCount)
            {
                payload["nextCursor"] = new QueryCursor
                {
                    Table = table.DbName,
                    Offset = offset + result.Rows.Count,
                    Limit = limit,
                    Sort = sortTokens,
                    Detail = detail,
                    Fields = fields,
                    FilterJson = filterElement is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } f
                        ? f.GetRawText()
                        : null,
                }.Encode();
                payload["message"] =
                    $"{totalCount} rows match; showing {result.Rows.Count} starting at offset {offset} — " +
                    "narrow with a filter, e.g. {\"status\":{\"_eq\":\"open\"}}, or pass page.cursor = nextCursor to continue.";
            }

            return payload;
        }

        private static int GetPageLimit(JsonElement? page)
        {
            if (page is not { } p || p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("limit", out var limitElement))
                return DefaultPageLimit;
            if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetInt32(out var limit)
                || limit < 1 || limit > MaxPageLimit)
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

            async Task<QueryIntentResult> RunAsync(GqlObjectQuery query) =>
                await executor.ExecuteAsync(new QueryIntent
                {
                    Query = query,
                    UserContext = new Dictionary<string, object?>(userContextProvider()),
                    Endpoint = endpoint,
                }, cancellationToken);

            // The row itself: every column, addressed by the full ordered key.
            var rowQuery = QueryToolCompiler.BuildQuery(table, table.Columns.OrderBy(c => c.OrdinalPosition));
            rowQuery.Filter = TableFilter.FromPrimaryKey(idValues, keyColumns, table.DbName);
            var rowResult = await RunAsync(rowQuery);
            var row = rowResult.Rows.FirstOrDefault()
                ?? throw new ToolPromptException(
                    $"No row found in '{table.DbName}' with id [{string.Join(", ", idValues)}]. " +
                    "It may not exist, or it may be outside your access scope (tenant and soft-delete rules apply). " +
                    "Use bifrost_query to search for candidate rows.");

            // Parents: one summary lookup per outgoing FK, so tenant/soft-delete
            // scope applies to each parent independently (an out-of-scope parent
            // reports found=false rather than leaking data).
            var parents = new JsonArray();
            foreach (var link in SchemaDescriber.OutgoingLinks(table))
                parents.Add(await BuildParentEntryAsync(link, row, RunAsync));

            // Children: count + first rows per incoming FK, ordered by the child
            // primary key for a deterministic top-N.
            var children = new JsonArray();
            foreach (var link in SchemaDescriber.IncomingLinks(table))
                children.Add(await BuildChildEntryAsync(link, row, RunAsync));

            return new JsonObject
            {
                ["table"] = table.DbName,
                ["id"] = new JsonArray(idValues.Select(ToJsonNode).ToArray()),
                ["row"] = ToJsonRow(row),
                ["parents"] = parents,
                ["children"] = children,
            };
        }

        private static async Task<JsonNode> BuildParentEntryAsync(
            TableLinkDto link,
            IReadOnlyDictionary<string, object?> row,
            Func<GqlObjectQuery, Task<QueryIntentResult>> runAsync)
        {
            var parentTable = link.ParentTable;
            var fkValues = link.ChildIds.Select(c => row.GetValueOrDefault(c.DbName)).ToList();
            var entry = new JsonObject
            {
                ["relationship"] = link.Name,
                ["table"] = parentTable.DbName,
                ["id"] = new JsonArray(fkValues.Select(ToJsonNode).ToArray()),
                ["displayName"] = null,
                ["found"] = false,
            };
            if (fkValues.Any(v => v is null))
                return entry;

            var displayColumn = SchemaDescriber.DisplayColumn(parentTable);
            var columns = parentTable.KeyColumns.ToList();
            if (displayColumn is not null && !columns.Contains(displayColumn))
                columns.Add(displayColumn);

            var parentQuery = QueryToolCompiler.BuildQuery(parentTable, columns);
            // Address the parent by the FULL ordered FK column list (composite FKs
            // AND every column pair; ParentIds/ChildIds are index-aligned).
            var filter = new Dictionary<string, object?>();
            for (var i = 0; i < link.ParentIds.Count; i++)
                filter[link.ParentIds[i].GraphQlName] = new Dictionary<string, object?> { ["_eq"] = fkValues[i] };
            parentQuery.Filter = TableFilter.FromObject(filter, parentTable.DbName);

            var parentRow = (await runAsync(parentQuery)).Rows.FirstOrDefault();
            if (parentRow is null)
                return entry; // FK points somewhere, but the row is outside the caller's scope.

            entry["found"] = true;
            entry["id"] = new JsonArray(parentTable.KeyColumns
                .Select(c => ToJsonNode(parentRow.GetValueOrDefault(c.DbName)))
                .ToArray());
            if (displayColumn is not null)
                entry["displayName"] = ToJsonNode(parentRow.GetValueOrDefault(displayColumn.DbName));
            return entry;
        }

        private static async Task<JsonNode> BuildChildEntryAsync(
            TableLinkDto link,
            IReadOnlyDictionary<string, object?> row,
            Func<GqlObjectQuery, Task<QueryIntentResult>> runAsync)
        {
            var childTable = link.ChildTable;
            var childQuery = QueryToolCompiler.BuildQuery(childTable, QueryToolCompiler.SummaryColumns(childTable));
            childQuery.Limit = ChildSummaryRowCount;
            childQuery.IncludeResult = true;
            childQuery.Sort = QueryToolCompiler.DefaultSort(childTable);

            // Match the FULL ordered FK column list, plus the polymorphic
            // discriminator when the link carries one (otherwise a shared child
            // table would count other parents' rows).
            var filter = new Dictionary<string, object?>();
            for (var i = 0; i < link.ChildIds.Count; i++)
                filter[link.ChildIds[i].GraphQlName] = new Dictionary<string, object?>
                {
                    ["_eq"] = row.GetValueOrDefault(link.ParentIds[i].DbName),
                };
            if (link.TypePredicate is { } predicate)
                filter[predicate.Column.GraphQlName] = new Dictionary<string, object?> { ["_eq"] = predicate.Value };
            childQuery.Filter = TableFilter.FromObject(filter, childTable.DbName);

            var result = await runAsync(childQuery);
            return new JsonObject
            {
                ["relationship"] = link.Name,
                ["table"] = childTable.DbName,
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

        // ---- shared helpers ----------------------------------------------------

        private static IDbTable ResolveTable(IDbModel model, string tableName) =>
            model.Tables.FirstOrDefault(t => string.Equals(t.DbName, tableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolPromptException(SchemaDescriber.UnknownTableMessage(model, tableName));

        private static JsonArray ToJsonRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
            new(rows.Select(r => (JsonNode?)ToJsonRow(r)).ToArray());

        private static JsonObject ToJsonRow(IReadOnlyDictionary<string, object?> row)
        {
            var obj = new JsonObject();
            foreach (var (column, value) in row)
                obj[column] = ToJsonNode(value);
            return obj;
        }

        private static JsonNode? ToJsonNode(object? value) =>
            value is null ? null : JsonSerializer.SerializeToNode(value);

        private static JsonElement? GetArgument(IDictionary<string, JsonElement>? args, string name) =>
            args is not null && args.TryGetValue(name, out var value) ? value : null;

        private static string? GetStringArgument(IDictionary<string, JsonElement>? args, string name)
        {
            var element = GetArgument(args, name);
            if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (e.ValueKind != JsonValueKind.String)
                throw new ToolPromptException($"Argument '{name}' must be a string.");
            return e.GetString();
        }

        private static string? GetString(JsonElement obj, string property)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new ToolPromptException($"'{property}' must be a string.");
            return value.GetString();
        }

        private static IReadOnlyList<string>? GetStringArray(IDictionary<string, JsonElement>? args, string name)
        {
            var element = GetArgument(args, name);
            if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (e.ValueKind != JsonValueKind.Array)
                throw new ToolPromptException($"Argument '{name}' must be an array of strings.");
            var values = new List<string>();
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new ToolPromptException($"Argument '{name}' must contain only strings.");
                values.Add(item.GetString()!);
            }
            return values;
        }

        private static JsonElement ParseSchema(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}

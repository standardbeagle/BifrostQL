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
    /// The <c>bifrost_search</c> MCP tool: a cross-table term search over every
    /// string column, one <c>_contains</c>-filtered intent per table through
    /// <see cref="IQueryIntentExecutor"/> — the term always binds as a SQL
    /// parameter and every per-table read passes the security transformer
    /// pipeline, so tenant scoping and soft-delete hiding exclude rows before
    /// they can match.
    ///
    /// <para>Fail-closed table skipping: when a table's intent is rejected at
    /// execution (e.g. it is tenant-filtered and the caller has no tenant
    /// context), that table is silently omitted from the results rather than
    /// failing the whole search or emitting an error entry. The caller cannot
    /// read the table at all, so surfacing the rejection per table would only
    /// add noise and advertise the existence of data the caller has no path
    /// to — mirroring how bifrost_row_context reports an out-of-scope parent
    /// as found=false instead of an error.</para>
    /// </summary>
    internal static class SearchTools
    {
        internal const string ToolName = "bifrost_search";

        /// <summary>Minimum trimmed term length — a 1-character term degenerates into a full-database scan.</summary>
        private const int MinTermLength = 2;
        private const int PerTableLimit = 5;
        private const int TotalLimit = 50;

        /// <summary>
        /// How many matching rows to pull per table before ranking. Ranking is a
        /// client-side re-sort by matched-column count, so it can only reorder rows
        /// it actually fetched — capping the SQL read at <see cref="PerTableLimit"/>
        /// would rank an already-truncated (PK-ordered) slice and silently drop
        /// higher-relevance rows. Fetching a larger candidate pool and keeping the
        /// top <see cref="PerTableLimit"/> makes the ranking meaningful; a table with
        /// more matches than this bound still ranks only its first candidates by PK,
        /// which the truncation message steers the caller past.
        /// </summary>
        private const int RankingCandidateLimit = 50;

        private static readonly JsonElement InputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "term": {
                  "type": "string",
                  "minLength": 2,
                  "description": "Text to search for (case-insensitive substring match across string columns). At least 2 characters."
                },
                "tables": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Restrict the search to these tables. Omit to search every table that has string columns."
                }
              },
              "required": ["term"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement OutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "term": { "type": "string" },
                "results": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "table": { "type": "string" },
                      "id": { "type": "array", "description": "Primary-key values in key-column order (usable as bifrost_row_context id)." },
                      "displayName": { "type": ["string", "null"] },
                      "matchedColumns": { "type": "array", "items": { "type": "string" }, "description": "String columns whose value contains the term; rows matching more columns rank first within their table." }
                    },
                    "required": ["table", "id", "displayName", "matchedColumns"]
                  }
                },
                "tables": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "table": { "type": "string" },
                      "totalMatches": { "type": "integer", "description": "Rows matching within your access scope, before the per-table cap." },
                      "returned": { "type": "integer" }
                    },
                    "required": ["table", "totalMatches", "returned"]
                  },
                  "description": "Per-table match totals for every table that had at least one match."
                },
                "message": { "type": "string", "description": "Steering guidance when results were truncated." }
              },
              "required": ["term", "results", "tables"]
            }
            """);

        public static Tool ToolDefinition() => new()
        {
            Name = ToolName,
            Title = "Search across tables",
            Description =
                "Case-insensitive substring search for a term across the string columns of every table (or a " +
                "supplied table list). Returns up to 5 ranked rows per table — id (usable with " +
                "bifrost_row_context), display name, and which columns matched — plus per-table match totals. " +
                "All reads pass through the server's security pipeline, so tenant scoping and soft-delete hiding " +
                "always apply.",
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
            var term = GetStringArgument(args, "term")?.Trim()
                ?? throw new ToolPromptException("Missing required argument 'term' (the text to search for).");
            if (term.Length < MinTermLength)
                throw new ToolPromptException(
                    $"term must be at least {MinTermLength} characters — a shorter term would match nearly every row.");

            var model = await executor.GetModelAsync(endpoint);
            var tables = ResolveSearchTables(model, GetStringArray(args, "tables"));

            var results = new JsonArray();
            var tableTotals = new JsonArray();
            var truncated = false;
            foreach (var table in tables)
            {
                if (results.Count >= TotalLimit)
                {
                    truncated = true;
                    break;
                }

                var stringColumns = StringColumns(table);
                QueryIntentResult result;
                try
                {
                    result = await executor.ExecuteAsync(new QueryIntent
                    {
                        Query = BuildSearchQuery(table, stringColumns, term),
                        UserContext = new Dictionary<string, object?>(userContextProvider()),
                        Endpoint = endpoint,
                    }, cancellationToken);
                }
                catch (BifrostExecutionError ex) when (ex.ErrorCode == BifrostExecutionError.AccessDeniedCode)
                {
                    // Fail-closed skip: an authorization transformer (tenant scoping,
                    // row/column policy) rejected the read (see class doc), so this
                    // table contributes nothing. Any other execution failure — a
                    // genuine DB error, a malformed intent — is a real fault and must
                    // propagate, not be silently reported as zero matches.
                    continue;
                }

                if (result.Rows.Count == 0)
                    continue;

                var displayColumn = SchemaDescriber.DisplayColumn(table);
                var ranked = result.Rows
                    .Select(row => (Row: row, Matched: MatchedColumns(stringColumns, row, term)))
                    .OrderByDescending(r => r.Matched.Count)
                    .ToList();

                var returned = 0;
                foreach (var (row, matched) in ranked)
                {
                    if (returned >= PerTableLimit)
                        break;
                    if (results.Count >= TotalLimit)
                    {
                        truncated = true;
                        break;
                    }
                    results.Add(new JsonObject
                    {
                        ["table"] = table.DbName,
                        ["id"] = new JsonArray(table.KeyColumns
                            .Select(c => ToJsonNode(row.GetValueOrDefault(c.DbName)))
                            .ToArray()),
                        ["displayName"] = displayColumn is not null
                            ? ToJsonNode(row.GetValueOrDefault(displayColumn.DbName))
                            : null,
                        ["matchedColumns"] = new JsonArray(matched.Select(c => (JsonNode?)c).ToArray()),
                    });
                    returned++;
                }

                var totalMatches = result.TotalCount ?? result.Rows.Count;
                truncated |= totalMatches > returned;
                tableTotals.Add(new JsonObject
                {
                    ["table"] = table.DbName,
                    ["totalMatches"] = totalMatches,
                    ["returned"] = returned,
                });
            }

            var payload = new JsonObject
            {
                ["term"] = term,
                ["results"] = results,
                ["tables"] = tableTotals,
            };
            if (truncated)
            {
                payload["message"] =
                    $"More rows match than shown (per-table cap {PerTableLimit}, overall cap {TotalLimit}). " +
                    "Page through a specific table with bifrost_query and a _contains filter, " +
                    "e.g. {\"<column>\":{\"_contains\":\"" + term + "\"}}.";
            }
            return payload;
        }

        /// <summary>
        /// Resolves the searched table set. Explicitly named tables must exist
        /// (did-you-mean prompt) and be searchable — asking to search a table
        /// with no string columns is a caller mistake worth failing fast on,
        /// whereas the automatic all-tables sweep simply skips such tables.
        /// </summary>
        private static List<IDbTable> ResolveSearchTables(IDbModel model, IReadOnlyList<string>? requested)
        {
            if (requested is null)
                return model.Tables
                    .Where(t => StringColumns(t).Count > 0)
                    .OrderBy(t => t.DbName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (requested.Count == 0)
                throw new ToolPromptException("tables must be a non-empty array of table names, or be omitted.");

            var tables = new List<IDbTable>();
            foreach (var name in requested)
            {
                var table = ResolveTable(model, name);
                if (StringColumns(table).Count == 0)
                    throw new ToolPromptException(
                        $"Table '{table.DbName}' has no string columns to search. " +
                        "Omit it, or use bifrost_query with a filter on its typed columns instead.");
                if (tables.All(t => t.DbName != table.DbName))
                    tables.Add(table);
            }
            return tables;
        }

        private static List<ColumnDto> StringColumns(IDbTable table) =>
            table.Columns
                .Where(c => SchemaDescriber.IsStringType(c.DataType))
                .OrderBy(c => c.OrdinalPosition)
                .ToList();

        /// <summary>
        /// One-table search intent: OR of <c>_contains</c> over every string
        /// column (the term binds as a SQL parameter through the standard filter
        /// machinery), projecting the key, display, and string columns needed to
        /// address, label, and rank the matches.
        /// </summary>
        private static GqlObjectQuery BuildSearchQuery(IDbTable table, IReadOnlyList<ColumnDto> stringColumns, string term)
        {
            var columns = new List<ColumnDto>(table.KeyColumns);
            if (SchemaDescriber.DisplayColumn(table) is { } display && !columns.Contains(display))
                columns.Add(display);
            foreach (var column in stringColumns)
                if (!columns.Contains(column))
                    columns.Add(column);

            var query = QueryToolCompiler.BuildQuery(table, columns);
            query.Limit = RankingCandidateLimit;
            query.IncludeResult = true;
            query.Sort = QueryToolCompiler.DefaultSort(table);

            Dictionary<string, object?> Condition(ColumnDto column) => new()
            {
                [column.GraphQlName] = new Dictionary<string, object?> { [FilterOperators.Contains] = term },
            };
            query.Filter = TableFilter.FromObject(
                stringColumns.Count == 1
                    ? Condition(stringColumns[0])
                    : new Dictionary<string, object?>
                    {
                        ["or"] = stringColumns.Select(c => (object)Condition(c)).ToList(),
                    },
                table.DbName);
            return query;
        }

        /// <summary>
        /// The string columns whose returned value contains the term,
        /// case-insensitively — the row's simple relevance signal. Judged
        /// client-side on the projected values because SQL reports only that the
        /// row matched, not which column did.
        /// </summary>
        private static List<string> MatchedColumns(
            IReadOnlyList<ColumnDto> stringColumns, IReadOnlyDictionary<string, object?> row, string term)
        {
            var matched = new List<string>();
            foreach (var column in stringColumns)
            {
                if (row.GetValueOrDefault(column.DbName) is string value
                    && value.Contains(term, StringComparison.OrdinalIgnoreCase))
                    matched.Add(column.ColumnName);
            }
            return matched;
        }
    }
}

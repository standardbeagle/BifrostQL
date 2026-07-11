using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Resolvers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Builds the <see cref="McpServerOptions"/> for a BifrostQL MCP server:
    /// two read-only schema tools plus schema resources, all backed by the
    /// endpoint's cached <see cref="Core.Model.IDbModel"/> resolved through
    /// <see cref="IQueryIntentExecutor.GetModelAsync"/> — never a private model
    /// loader, so the MCP surface always sees exactly the model the query
    /// pipeline executes against.
    ///
    /// <para><b>Tools</b> (all <c>readOnlyHint</c>, with output schemas and
    /// structured content):</para>
    /// <list type="bullet">
    /// <item><c>bifrost_schema_overview</c> — dense curated map of the database:
    /// tables, keys, relationship edges, behavior notes.</item>
    /// <item><c>bifrost_describe_table</c> — one table in depth: columns, keys,
    /// foreign keys both directions, behavior notes. Unknown table names return a
    /// prompt-style error with a nearest-name suggestion and the table list.</item>
    /// </list>
    ///
    /// <para><b>Resources</b>: <c>bifrost://schema/overview</c> (full-detail
    /// overview) and <c>bifrost://schema/{table}</c> per table, serving the same
    /// JSON payloads as the tools.</para>
    /// </summary>
    public static class BifrostMcpServerFactory
    {
        internal const string ServerName = "BifrostQL";
        internal const string SchemaOverviewToolName = "bifrost_schema_overview";
        internal const string DescribeTableToolName = "bifrost_describe_table";
        internal const string OverviewResourceUri = "bifrost://schema/overview";
        internal const string TableResourceUriPrefix = "bifrost://schema/";
        private const string JsonMimeType = "application/json";

        private static readonly JsonElement SchemaOverviewInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "detail": {
                  "type": "string",
                  "enum": ["summary", "full"],
                  "default": "summary",
                  "description": "summary = tables, keys, relationships, behavior notes; full = additionally inlines a condensed column list per table."
                }
              },
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement SchemaOverviewOutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "detail": { "type": "string", "enum": ["summary", "full"] },
                "tableCount": { "type": "integer" },
                "tables": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "schema": { "type": "string" },
                      "primaryKey": { "type": "array", "items": { "type": "string" } },
                      "columnCount": { "type": "integer" },
                      "references": { "type": "array", "items": { "type": "string" }, "description": "Outgoing foreign keys as 'fkColumn -> table.column' edges." },
                      "referencedBy": { "type": "array", "items": { "type": "string" }, "description": "Incoming foreign keys as 'table.fkColumn -> column' edges." },
                      "notes": { "type": "array", "items": { "type": "string" } },
                      "columns": { "type": "array", "items": { "type": "string" }, "description": "Only present with detail=full: condensed 'name: type markers' strings." }
                    },
                    "required": ["name", "schema", "primaryKey", "columnCount", "references", "referencedBy", "notes"]
                  }
                }
              },
              "required": ["detail", "tableCount", "tables"]
            }
            """);

        private static readonly JsonElement DescribeTableInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": {
                  "type": "string",
                  "description": "Database table name exactly as listed by bifrost_schema_overview."
                }
              },
              "required": ["table"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement DescribeTableOutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string" },
                "schema": { "type": "string" },
                "primaryKey": { "type": "array", "items": { "type": "string" } },
                "columns": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "type": { "type": "string" },
                      "nullable": { "type": "boolean" },
                      "primaryKey": { "type": "boolean" },
                      "identity": { "type": "boolean" },
                      "unique": { "type": "boolean" }
                    },
                    "required": ["name", "type", "nullable", "primaryKey", "identity", "unique"]
                  }
                },
                "foreignKeysOut": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "columns": { "type": "array", "items": { "type": "string" } },
                      "referencesTable": { "type": "string" },
                      "referencesColumns": { "type": "array", "items": { "type": "string" } }
                    },
                    "required": ["columns", "referencesTable", "referencesColumns"]
                  }
                },
                "foreignKeysIn": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "table": { "type": "string" },
                      "columns": { "type": "array", "items": { "type": "string" } },
                      "referencesColumns": { "type": "array", "items": { "type": "string" } }
                    },
                    "required": ["table", "columns", "referencesColumns"]
                  }
                },
                "behaviorNotes": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["table", "schema", "primaryKey", "columns", "foreignKeysOut", "foreignKeysIn", "behaviorNotes"]
            }
            """);

        /// <summary>
        /// Creates the server options for one BifrostQL endpoint. <paramref name="endpoint"/>
        /// is the registered GraphQL endpoint path; null selects the single registered
        /// endpoint and an unknown path fails fast on first use (no fallback).
        /// <paramref name="userContextProvider"/> supplies the caller identity
        /// (tenant id, roles, …) applied to every row-reading intent; the default
        /// is an EMPTY context, so tenant-filtered tables fail closed exactly like
        /// an unauthenticated GraphQL request (stdio dev mode has no per-request
        /// principal).
        /// </summary>
        public static McpServerOptions CreateServerOptions(
            IQueryIntentExecutor executor,
            string? endpoint = null,
            Func<IDictionary<string, object?>>? userContextProvider = null)
        {
            if (executor is null) throw new ArgumentNullException(nameof(executor));
            var contextProvider = userContextProvider ?? (() => new Dictionary<string, object?>());

            return new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = typeof(BifrostMcpServerFactory).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                },
                ServerInstructions =
                    "BifrostQL exposes a SQL database. Start with bifrost_schema_overview to map the schema, " +
                    "then bifrost_describe_table for column-level detail. Read rows with bifrost_query " +
                    "(structured filter + cursor pagination) and fetch one row with its related parent/child " +
                    "context via bifrost_row_context. Compute grouped counts and numeric sums/averages with " +
                    "bifrost_aggregate, and locate rows by text across tables with bifrost_search. " +
                    "Behavior notes describe server-enforced semantics " +
                    "(tenant scoping, hidden soft-deleted rows) that apply to all data access.",
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability(),
                    Resources = new ResourcesCapability(),
                },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = BuildTools() }),
                    CallToolHandler = (request, ct) => CallToolAsync(executor, endpoint, contextProvider, request.Params, ct),
                    ListResourcesHandler = (_, ct) => ListResourcesAsync(executor, endpoint, ct),
                    ReadResourceHandler = (request, ct) => ReadResourceAsync(executor, endpoint, request.Params, ct),
                },
            };
        }

        private static List<Tool> BuildTools() =>
        [
            new Tool
            {
                Name = SchemaOverviewToolName,
                Title = "Database schema overview",
                Description =
                    "Curated map of the entire database: every table with its primary key, foreign-key " +
                    "relationship edges (both directions), and behavior notes. Row counts and sample values " +
                    "are not included. Use detail=full to inline condensed per-table column lists.",
                InputSchema = SchemaOverviewInputSchema,
                OutputSchema = SchemaOverviewOutputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
            },
            new Tool
            {
                Name = DescribeTableToolName,
                Title = "Describe one table",
                Description =
                    "Column-level detail for one table: columns with types and nullability, primary key, " +
                    "foreign keys in both directions, and behavior notes for server-enforced semantics " +
                    "(tenant scoping, soft-delete hiding). Sample values are not included.",
                InputSchema = DescribeTableInputSchema,
                OutputSchema = DescribeTableOutputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
            },
            DataTools.QueryToolDefinition(),
            DataTools.RowContextToolDefinition(),
            AggregateTools.ToolDefinition(),
            SearchTools.ToolDefinition(),
        ];

        private static async ValueTask<CallToolResult> CallToolAsync(
            IQueryIntentExecutor executor, string? endpoint, Func<IDictionary<string, object?>> userContextProvider,
            CallToolRequestParams? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parameters is null)
                throw new McpProtocolException("Missing tool call parameters.", McpErrorCode.InvalidParams);

            // Row-reading tools: argument mistakes surface as prompt-style tool
            // errors (ToolPromptException), and execution-layer rejections
            // (missing tenant context, policy-denied column, unsupported filter
            // shape) surface the same way — both are actionable by the calling
            // agent, so neither becomes a protocol fault.
            if (parameters.Name is DataTools.QueryToolName or DataTools.RowContextToolName
                or AggregateTools.ToolName or SearchTools.ToolName)
            {
                try
                {
                    var payload = parameters.Name switch
                    {
                        DataTools.QueryToolName => await DataTools.ExecuteQueryAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                        DataTools.RowContextToolName => await DataTools.ExecuteRowContextAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                        AggregateTools.ToolName => await AggregateTools.ExecuteAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                        _ => await SearchTools.ExecuteAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                    };
                    return StructuredResult(payload);
                }
                catch (ToolPromptException e)
                {
                    return ErrorResult(e.Message);
                }
                catch (BifrostExecutionError e)
                {
                    return ErrorResult(e.Message);
                }
            }

            var model = await executor.GetModelAsync(endpoint);
            switch (parameters.Name)
            {
                case SchemaOverviewToolName:
                {
                    var detail = GetStringArgument(parameters, "detail") ?? "summary";
                    if (detail is not ("summary" or "full"))
                        return ErrorResult($"Invalid detail '{detail}'. Allowed values: summary, full.");
                    return StructuredResult(SchemaDescriber.BuildOverview(model, fullDetail: detail == "full"));
                }
                case DescribeTableToolName:
                {
                    var tableName = GetStringArgument(parameters, "table");
                    if (string.IsNullOrWhiteSpace(tableName))
                        return ErrorResult("Missing required argument 'table'. Call bifrost_schema_overview to list the available tables.");
                    var table = model.Tables.FirstOrDefault(t =>
                        string.Equals(t.DbName, tableName, StringComparison.OrdinalIgnoreCase));
                    if (table is null)
                        return ErrorResult(SchemaDescriber.UnknownTableMessage(model, tableName));
                    return StructuredResult(SchemaDescriber.BuildTableDescription(table));
                }
                default:
                    throw new McpProtocolException($"Unknown tool '{parameters.Name}'.", McpErrorCode.InvalidParams);
            }
        }

        private static async ValueTask<ListResourcesResult> ListResourcesAsync(
            IQueryIntentExecutor executor, string? endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = await executor.GetModelAsync(endpoint);
            var resources = new List<Resource>
            {
                new()
                {
                    Uri = OverviewResourceUri,
                    Name = "schema-overview",
                    Title = "Database schema overview",
                    Description = "Full-detail schema map: every table with columns, keys, relationships, and behavior notes.",
                    MimeType = JsonMimeType,
                },
            };
            resources.AddRange(model.Tables
                .OrderBy(t => t.DbName, StringComparer.OrdinalIgnoreCase)
                .Select(t => new Resource
                {
                    Uri = TableResourceUriPrefix + Uri.EscapeDataString(t.DbName),
                    Name = t.DbName,
                    Title = $"Table {t.DbName}",
                    Description = $"Columns, keys, foreign keys, and behavior notes for table '{t.DbName}'.",
                    MimeType = JsonMimeType,
                }));
            return new ListResourcesResult { Resources = resources };
        }

        private static async ValueTask<ReadResourceResult> ReadResourceAsync(
            IQueryIntentExecutor executor, string? endpoint, ReadResourceRequestParams? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = parameters?.Uri;
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith(TableResourceUriPrefix, StringComparison.Ordinal))
                throw new McpProtocolException(
                    $"Unknown resource '{uri}'. Schema resources live under {TableResourceUriPrefix}: {OverviewResourceUri} or {TableResourceUriPrefix}{{table}}.",
                    McpErrorCode.ResourceNotFound);

            var model = await executor.GetModelAsync(endpoint);
            if (string.Equals(uri, OverviewResourceUri, StringComparison.Ordinal))
                return JsonResource(uri, SchemaDescriber.BuildOverview(model, fullDetail: true));

            var tableName = Uri.UnescapeDataString(uri.Substring(TableResourceUriPrefix.Length));
            var table = model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, tableName, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                throw new McpProtocolException(SchemaDescriber.UnknownTableMessage(model, tableName), McpErrorCode.ResourceNotFound);
            return JsonResource(uri, SchemaDescriber.BuildTableDescription(table));
        }

        private static string? GetStringArgument(CallToolRequestParams parameters, string name)
        {
            if (parameters.Arguments is null || !parameters.Arguments.TryGetValue(name, out var value))
                return null;
            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
        }

        private static CallToolResult StructuredResult(JsonObject payload) => new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(payload, McpJsonUtilities.DefaultOptions),
            Content = [new TextContentBlock { Text = payload.ToJsonString() }],
        };

        private static CallToolResult ErrorResult(string message) => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };

        private static ReadResourceResult JsonResource(string uri, JsonObject payload) => new()
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = JsonMimeType,
                    Text = payload.ToJsonString(),
                },
            ],
        };

        private static JsonElement ParseSchema(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}

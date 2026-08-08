using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using Microsoft.Extensions.Logging;
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
            Func<IDictionary<string, object?>>? userContextProvider = null,
            IMutationIntentExecutor? mutationExecutor = null,
            bool enableWrites = false,
            McpToolPolicyOptions? toolPolicy = null,
            ILogger? logger = null,
            DeclarativeToolDocument? declarativeTools = null)
        {
            if (executor is null) throw new ArgumentNullException(nameof(executor));
            // Reject name collisions/duplicates before publishing any surface so a
            // declarative tool can never shadow a built-in or another declarative tool.
            ValidateDeclarativeToolNames(declarativeTools);
            var contextProvider = userContextProvider ?? (() => new Dictionary<string, object?>());
            var policy = toolPolicy ?? new McpToolPolicyOptions();

            // The write surface is OFF by construction: it only lists (and only
            // dispatches) the write tools when a deployment has both supplied a
            // mutation executor AND opted in. A disabled surface builds zero intent
            // and cannot be probed for behavior (protocol-adapter-security invariant 7).
            var writesActive = enableWrites && mutationExecutor is not null;

            // Enforce the tool-budget guardrail against the FULL declared surface (before any
            // per-identity filtering narrows it): warn past the configured threshold, fail load
            // past the hard cap. Declarative tools count against the same budget as built-ins.
            // Both thresholds are options, not constants baked in here.
            var declaredTools = BuildTools(writesActive);
            // Declared read tools always count; declared write tools only count against
            // the budget when the write surface is active (otherwise they are not listed).
            var declarativeCount = declarativeTools is null ? 0
                : declarativeTools.Tools.Count(tool => !tool.IsMutation)
                  + (writesActive ? declarativeTools.Tools.Count(tool => tool.IsMutation) : 0);
            EnforceToolBudget(declaredTools.Count + declarativeCount, policy.Budget, logger);

            // Per-identity tool filtering (fail-closed). Built once from the policy so tools/list and
            // tools/call agree: a role-gated tool hidden from a caller's list is also refused by name.
            var gate = McpToolAccessGate.From(policy);

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
                    ListToolsHandler = (_, ct) => MapProtocolConditionsAsync(async () =>
                    {
                        var tools = await BuildListedToolsAsync(executor, endpoint, declaredTools, declarativeTools, writesActive, ct);
                        return new ListToolsResult
                        {
                            Tools = gate.GatesAnything
                                ? gate.Filter(tools, ResolveRoles(contextProvider))
                                : tools,
                        };
                    }),
                    CallToolHandler = (request, ct) => CallToolAsync(
                        executor, mutationExecutor, writesActive, gate, endpoint, contextProvider, declarativeTools, request.Params, ct),
                    ListResourcesHandler = (_, ct) => MapProtocolConditionsAsync(
                        () => ListResourcesAsync(executor, endpoint, ct)),
                    ReadResourceHandler = (request, ct) => MapProtocolConditionsAsync(
                        () => ReadResourceAsync(executor, endpoint, request.Params, ct)),
                },
            };
        }

        private static List<Tool> BuildTools(bool writesActive)
        {
            var tools = BuildReadTools();
            if (writesActive)
                tools.AddRange(WriteTools.ToolDefinitions());
            return tools;
        }

        /// <summary>
        /// Rejects a declarative document whose tool names collide with a built-in
        /// (read or write) tool or with each other. Runs before any surface is
        /// published so tools/list and tools/call can never be ambiguous.
        /// </summary>
        private static void ValidateDeclarativeToolNames(DeclarativeToolDocument? document)
        {
            if (document is null) return;
            var reserved = new HashSet<string>(StringComparer.Ordinal)
            {
                SchemaOverviewToolName, DescribeTableToolName,
                DataTools.QueryToolName, DataTools.RowContextToolName,
                AggregateTools.ToolName, SearchTools.ToolName,
                WriteTools.InsertToolName, WriteTools.UpdateToolName, WriteTools.DeleteToolName,
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in document.Tools)
            {
                if (reserved.Contains(tool.Name))
                    throw new InvalidOperationException($"Declarative MCP tool '{tool.Name}' conflicts with a built-in tool name.");
                if (!seen.Add(tool.Name))
                    throw new InvalidOperationException($"Declarative MCP tool name '{tool.Name}' is declared more than once.");
            }
        }

        /// <summary>
        /// Builds the full listed tool surface: the always-declared built-in tools
        /// plus any declarative tools, whose surfaces are compiled against the
        /// endpoint's cached model (compilation fails the list before publishing an
        /// invalid tool). Per-identity gating is applied by the caller.
        /// </summary>
        private static async ValueTask<List<Tool>> BuildListedToolsAsync(
            IQueryIntentExecutor executor, string? endpoint,
            List<Tool> declaredTools, DeclarativeToolDocument? declarativeTools, bool writesActive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (declarativeTools is null || declarativeTools.Tools.Count == 0)
                return declaredTools;

            var tools = new List<Tool>(declaredTools);
            var model = await executor.GetModelAsync(endpoint);
            foreach (var definition in declarativeTools.Tools)
            {
                if (definition.IsMutation)
                {
                    // Declared write tools are listed ONLY when the write surface is active,
                    // so a disabled deployment never advertises a write tool at all.
                    if (writesActive)
                        tools.Add(DeclarativeMutationTool.BuildTool(definition));
                    continue;
                }
                // Compile eagerly so an invalid declarative tool fails the list rather than
                // publishing a surface that would only fault on call.
                _ = DeclarativeQueryToolCompiler.Compile(definition, model, executor, endpoint);
                tools.Add(DeclarativeToolSurface.BuildTool(definition, model));
            }
            return tools;
        }

        /// <summary>
        /// Applies the tool-budget guardrail to <paramref name="declaredCount"/>: throws a precise
        /// load-time error past the hard cap (naming the count, cap, and warn threshold) and logs a
        /// consolidation warning past the warn threshold. Neither threshold is a baked-in constant —
        /// both come from <paramref name="budget"/>.
        /// </summary>
        private static void EnforceToolBudget(int declaredCount, McpToolBudgetOptions budget, ILogger? logger)
        {
            if (declaredCount > budget.HardCap)
                throw new InvalidOperationException(
                    $"MCP tool surface declares {declaredCount} tools, exceeding the hard cap of {budget.HardCap} " +
                    $"(warn threshold {budget.WarnThreshold}). Consolidate related operations into fewer, richer " +
                    "tools, or raise McpToolBudgetOptions.HardCap deliberately.");

            if (declaredCount > budget.WarnThreshold)
                logger?.LogWarning(
                    "MCP tool surface declares {DeclaredToolCount} tools, over the warn threshold of {WarnThreshold} " +
                    "(hard cap {HardCap}). Prefer consolidating related operations into fewer, richer tools.",
                    declaredCount, budget.WarnThreshold, budget.HardCap);
        }

        /// <summary>
        /// Resolves the caller's roles for tool gating from the SAME user context every data path uses —
        /// projected by <see cref="IBifrostAuthContextFactory"/> and read through
        /// <see cref="PolicyIdentity.ExtractRoles"/> (no bespoke claim reading here). A token from an
        /// unmapped OIDC issuer fails closed to no roles, so every role-gated tool stays hidden.
        /// </summary>
        private static IReadOnlyCollection<string> ResolveRoles(Func<IDictionary<string, object?>> userContextProvider)
        {
            try
            {
                return PolicyIdentity.ExtractRoles(userContextProvider());
            }
            catch (UnmappedOidcIssuerException)
            {
                return Array.Empty<string>();
            }
        }

        private static List<Tool> BuildReadTools() =>
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

        /// <summary>
        /// THE error funnel for <c>tools/call</c>. Every op class — schema tools, data
        /// tools, built-in writes, declarative reads and writes — dispatches inside this
        /// ONE try/catch, so cross-op divergence is impossible by construction: there is
        /// no second catch to drift out of sync (protocol-adapter-security invariants 9
        /// and 10). Before it, only the data and declarative branches carried a catch, so
        /// an identical condition (a failed endpoint/model resolution, a policy denial)
        /// surfaced as a sanitized <c>isError</c> result on <c>bifrost_query</c> but as an
        /// unhandled JSON-RPC fault on <c>bifrost_schema_overview</c> — a differential
        /// wire signal for one condition.
        /// </summary>
        private static async ValueTask<CallToolResult> CallToolAsync(
            IQueryIntentExecutor executor, IMutationIntentExecutor? mutationExecutor, bool writesActive,
            McpToolAccessGate gate, string? endpoint, Func<IDictionary<string, object?>> userContextProvider,
            DeclarativeToolDocument? declarativeTools,
            CallToolRequestParams? parameters, CancellationToken cancellationToken)
        {
            try
            {
                return await DispatchToolAsync(
                    executor, mutationExecutor, writesActive, gate, endpoint, userContextProvider,
                    declarativeTools, parameters, cancellationToken);
            }
            catch (Exception e) when (IsMappedCondition(e))
            {
                return ErrorResult(MapConditionMessage(e));
            }
        }

        /// <summary>
        /// The conditions the funnel owns. Kept as ONE predicate shared by the tool funnel
        /// and the resource funnel so the two never drift into catching different sets for
        /// the same seam (invariant 9's catch-set symmetry). <see cref="McpProtocolException"/>
        /// is deliberately excluded: it is the adapter's own protocol-level fault type and
        /// is already the intended wire shape.
        /// </summary>
        /// <summary>
        /// The funnel for the op classes whose wire shape has no <c>isError</c> field
        /// (<c>tools/list</c>, <c>resources/list</c>, <c>resources/read</c>). It catches the
        /// IDENTICAL condition set as the <c>tools/call</c> funnel — the catch-set symmetry
        /// invariant 9 demands — and maps it onto the only shape those responses have, a
        /// protocol error with the same sanitized message. Without it, a condition that is
        /// a clean <c>isError</c> on <c>tools/call</c> escaped these handlers unhandled.
        /// </summary>
        private static async ValueTask<T> MapProtocolConditionsAsync<T>(Func<ValueTask<T>> operation)
        {
            try
            {
                return await operation();
            }
            catch (Exception e) when (IsMappedCondition(e))
            {
                throw new McpProtocolException(MapConditionMessage(e), McpErrorCode.InvalidRequest);
            }
        }

        private static bool IsMappedCondition(Exception e) =>
            e is UnmappedOidcIssuerException or ToolPromptException or BifrostExecutionError;

        /// <summary>
        /// Maps a funnelled condition to its client-facing message. An unmapped OIDC issuer
        /// is SANITIZED — its own message names the issuer, which never reaches the wire
        /// (invariant 3); the specific issuer stays in server-side logs.
        /// </summary>
        private static string MapConditionMessage(Exception e) => e is UnmappedOidcIssuerException
            ? "Authentication failed: the presented token could not be resolved to an identity."
            : e.Message;

        private static async ValueTask<CallToolResult> DispatchToolAsync(
            IQueryIntentExecutor executor, IMutationIntentExecutor? mutationExecutor, bool writesActive,
            McpToolAccessGate gate, string? endpoint, Func<IDictionary<string, object?>> userContextProvider,
            DeclarativeToolDocument? declarativeTools,
            CallToolRequestParams? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parameters is null)
                throw new McpProtocolException("Missing tool call parameters.", McpErrorCode.InvalidParams);

            // ENABLE GATE FIRST for the built-in write tools — before role gating, argument
            // parsing, model lookup, or intent construction. A disabled write surface builds
            // zero intent and cannot be probed for behavior (protocol-adapter-security
            // invariant 7b). Symmetric with the declarative write branch below, which gates
            // the same way and returns the same constant message: falling through to the
            // generic "Unknown tool" fault made the disabled surface probeable, because
            // reaching it first ran a live model load.
            if (!writesActive && WriteTools.IsWriteTool(parameters.Name))
                return DisabledWriteSurfaceResult(parameters.Name);

            // Fail-closed tool gating: refuse a role-gated tool the caller may not see BEFORE building any
            // intent, so a hidden tool cannot be invoked by name even though it was excluded from the list.
            // Roles come only from the shared factory's user context (ResolveRoles); an unresolved identity
            // has no roles and is refused.
            if (gate.GatesAnything && gate.IsGated(parameters.Name)
                && !gate.IsVisible(parameters.Name, ResolveRoles(userContextProvider)))
            {
                return ErrorResult($"Tool '{parameters.Name}' is not available for the current identity.");
            }

            // Data tools (read + write): argument mistakes surface as prompt-style tool
            // errors (ToolPromptException), and execution-layer rejections (missing tenant
            // context, policy-denied column, unsupported filter shape) surface the same
            // way — both are actionable by the calling agent, so neither becomes a
            // protocol fault. The mapping lives in the caller's single funnel.
            var isReadTool = parameters.Name is DataTools.QueryToolName or DataTools.RowContextToolName
                or AggregateTools.ToolName or SearchTools.ToolName;
            if (isReadTool || (writesActive && WriteTools.IsWriteTool(parameters.Name)))
            {
                var payload = parameters.Name switch
                {
                    DataTools.QueryToolName => await DataTools.ExecuteQueryAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                    DataTools.RowContextToolName => await DataTools.ExecuteRowContextAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                    AggregateTools.ToolName => await AggregateTools.ExecuteAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                    SearchTools.ToolName => await SearchTools.ExecuteAsync(executor, endpoint, userContextProvider, parameters, cancellationToken),
                    _ => await WriteTools.ExecuteAsync(mutationExecutor!, endpoint, userContextProvider, parameters, cancellationToken),
                };
                return StructuredResult(payload);
            }

            // Declarative tools: same unskippable intent path (transformers apply) and the
            // same funnel as every other op class. The gate check above already refused any
            // role-gated declarative tool the caller may not see.
            var declarative = declarativeTools?.Tools.FirstOrDefault(tool => tool.Name == parameters.Name);
            if (declarative is { IsMutation: true })
            {
                // ENABLE GATE FIRST — before any param validation, model lookup, or intent
                // construction. A disabled write surface builds zero intent and cannot be
                // probed for behavior (protocol-adapter-security invariant 7). writesActive
                // implies a non-null mutationExecutor by construction.
                if (!writesActive)
                    return DisabledWriteSurfaceResult(parameters.Name);

                IReadOnlyDictionary<string, JsonElement> mutationArgs = parameters.Arguments is null
                    ? new Dictionary<string, JsonElement>()
                    : new Dictionary<string, JsonElement>(parameters.Arguments);
                return StructuredResult(await DeclarativeMutationTool.ExecuteAsync(
                    mutationExecutor!, declarative, endpoint, mutationArgs, userContextProvider(), cancellationToken));
            }
            if (declarative is not null)
            {
                var declarativeModel = await executor.GetModelAsync(endpoint);
                IReadOnlyDictionary<string, JsonElement> arguments = parameters.Arguments is null
                    ? new Dictionary<string, JsonElement>()
                    : new Dictionary<string, JsonElement>(parameters.Arguments);
                return StructuredResult(await DeclarativeToolSurface.ExecuteAsync(
                    declarative, declarativeModel, executor, endpoint,
                    arguments, userContextProvider(), cancellationToken));
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

        /// <summary>
        /// The single refusal every disabled write tool returns — built-in and declarative
        /// alike, so the two op classes give the identical wire signal for the identical
        /// condition (invariant 9). It is a constant: it depends on nothing but the tool
        /// name, so it carries no probe channel.
        /// </summary>
        private static CallToolResult DisabledWriteSurfaceResult(string toolName) => ErrorResult(
            $"Tool '{toolName}' is a write tool and the MCP write surface is disabled. " +
            "Enable writes on the server to use it.");

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

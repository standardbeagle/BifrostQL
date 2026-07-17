using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Resolvers;
using ModelContextProtocol.Protocol;
using static BifrostQL.Mcp.ToolJson;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// The MCP write surface: <c>bifrost_insert</c>, <c>bifrost_update</c>, and
    /// <c>bifrost_delete</c>, each routed EXCLUSIVELY through
    /// <see cref="IMutationIntentExecutor"/> (→ the full <c>TableMutationPipeline</c>:
    /// tenant scoping, audit actor resolution, soft-delete rewrite,
    /// field-encryption-on-write, CDC/history hooks). This mirrors the read tools'
    /// contract on the write side: there is no code path here that renders SQL,
    /// builds a WHERE predicate, or special-cases soft-delete — the adapter supplies
    /// only the table, the caller's column values, the positional primary key, and
    /// the session's user context, and the pipeline decides every security-relevant
    /// outcome (see protocol-adapter-security invariant 7).
    ///
    /// <para><b>Fail-closed by construction.</b> The whole surface is OFF unless a
    /// deployment sets <see cref="McpAuthOptions.EnableWrites"/>. When disabled the
    /// three tools are never listed, so a disabled surface builds zero intent and
    /// cannot be probed for behavior; enabling it is a posture change the adapter
    /// logs as a startup warning (mirroring the RESP <c>EnableWrites</c> opt-in).</para>
    /// </summary>
    internal static class WriteTools
    {
        internal const string InsertToolName = "bifrost_insert";
        internal const string UpdateToolName = "bifrost_update";
        internal const string DeleteToolName = "bifrost_delete";

        internal static bool IsWriteTool(string? name) =>
            name is InsertToolName or UpdateToolName or DeleteToolName;

        private static readonly JsonElement InsertInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string", "description": "Database table name exactly as listed by bifrost_schema_overview." },
                "values": { "type": "object", "description": "Column values for the new row, keyed by column name. Server-enforced columns (tenant id) are pinned by the pipeline and cannot be overridden." }
              },
              "required": ["table", "values"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement UpdateInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string", "description": "Database table name exactly as listed by bifrost_schema_overview." },
                "id": { "description": "Primary-key value of the row to update. Composite keys: an array in primary-key column order, or a 'v1|v2' delimited string. Never just the first key column." },
                "set": { "type": "object", "description": "Column values to change, keyed by column name." }
              },
              "required": ["table", "id", "set"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement DeleteInputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string", "description": "Database table name exactly as listed by bifrost_schema_overview." },
                "id": { "description": "Primary-key value of the row to delete. Composite keys: an array in primary-key column order, or a 'v1|v2' delimited string. On a soft-delete table the row is marked deleted, not physically removed." }
              },
              "required": ["table", "id"],
              "additionalProperties": false
            }
            """);

        private static readonly JsonElement WriteOutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string" },
                "action": { "type": "string", "enum": ["insert", "update", "delete"] },
                "result": { "description": "Insert: the generated identity. Update/Delete: the number of rows affected within your access scope (0 when the row is outside it)." }
              },
              "required": ["table", "action", "result"]
            }
            """);

        internal static IEnumerable<Tool> ToolDefinitions()
        {
            yield return new Tool
            {
                Name = InsertToolName,
                Title = "Insert a row",
                Description =
                    "Insert one new row. The write passes through the server's mutation pipeline — tenant id is " +
                    "pinned to your identity (any client-supplied tenant value is overridden), and all validation, " +
                    "encryption-on-write and audit hooks apply. Returns the generated identity.",
                InputSchema = InsertInputSchema,
                OutputSchema = WriteOutputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false },
            };
            yield return new Tool
            {
                Name = UpdateToolName,
                Title = "Update a row by primary key",
                Description =
                    "Update one row addressed by its primary key. The pipeline ANDs your tenant scope onto the " +
                    "write, so a row outside your scope matches nothing and affects zero rows. Returns the number " +
                    "of rows affected.",
                InputSchema = UpdateInputSchema,
                OutputSchema = WriteOutputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true },
            };
            yield return new Tool
            {
                Name = DeleteToolName,
                Title = "Delete a row by primary key",
                Description =
                    "Delete one row addressed by its primary key. On a table configured for soft-delete the row is " +
                    "marked deleted (not physically removed); the pipeline decides. Your tenant scope is applied, so " +
                    "a row outside your scope affects zero rows. Returns the number of rows affected.",
                InputSchema = DeleteInputSchema,
                OutputSchema = WriteOutputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = true },
            };
        }

        internal static async Task<JsonObject> ExecuteAsync(
            IMutationIntentExecutor mutationExecutor,
            string? endpoint,
            Func<IDictionary<string, object?>> userContextProvider,
            CallToolRequestParams parameters,
            CancellationToken cancellationToken)
        {
            var args = parameters.Arguments;
            var tableName = GetStringArgument(args, "table")
                ?? throw new ToolPromptException(
                    "Missing required argument 'table'. Call bifrost_schema_overview to list the available tables.");

            var intent = parameters.Name switch
            {
                InsertToolName => BuildInsert(tableName, args, endpoint, userContextProvider),
                UpdateToolName => BuildUpdate(tableName, args, endpoint, userContextProvider),
                DeleteToolName => BuildDelete(tableName, args, endpoint, userContextProvider),
                _ => throw new ToolPromptException($"Unknown write tool '{parameters.Name}'."),
            };

            var result = await mutationExecutor.ExecuteAsync(intent, cancellationToken);

            // Insert reports the generated identity; update/delete report the real
            // affected-row count. For a single-key update the pipeline's Value is the
            // KEY, never a count — invariant 8(b) — so surface AffectedRows for update
            // and the delete's Value (which already IS the affected count).
            var (action, resultValue) = parameters.Name switch
            {
                InsertToolName => ("insert", result.Value),
                UpdateToolName => ("update", (object?)(result.AffectedRows ?? 0)),
                _ => ("delete", result.Value),
            };

            return new JsonObject
            {
                ["table"] = tableName,
                ["action"] = action,
                ["result"] = ToJsonNode(resultValue),
            };
        }

        private static MutationIntent BuildInsert(
            string table, IDictionary<string, JsonElement>? args, string? endpoint,
            Func<IDictionary<string, object?>> userContextProvider)
        {
            var values = ReadColumnObject(args, "values");
            if (values.Count == 0)
                throw new ToolPromptException("Argument 'values' must contain at least one column value.");
            return new MutationIntent
            {
                Table = table,
                Action = MutationIntentAction.Insert,
                Data = values,
                UserContext = new Dictionary<string, object?>(userContextProvider()),
                Endpoint = endpoint,
            };
        }

        private static MutationIntent BuildUpdate(
            string table, IDictionary<string, JsonElement>? args, string? endpoint,
            Func<IDictionary<string, object?>> userContextProvider)
        {
            var set = ReadColumnObject(args, "set");
            if (set.Count == 0)
                throw new ToolPromptException("Argument 'set' must contain at least one column to change.");
            return new MutationIntent
            {
                Table = table,
                Action = MutationIntentAction.Update,
                Data = set,
                PrimaryKey = ReadPrimaryKey(args),
                UserContext = new Dictionary<string, object?>(userContextProvider()),
                Endpoint = endpoint,
            };
        }

        private static MutationIntent BuildDelete(
            string table, IDictionary<string, JsonElement>? args, string? endpoint,
            Func<IDictionary<string, object?>> userContextProvider)
            => new()
            {
                Table = table,
                Action = MutationIntentAction.Delete,
                Data = new Dictionary<string, object?>(),
                PrimaryKey = ReadPrimaryKey(args),
                UserContext = new Dictionary<string, object?>(userContextProvider()),
                Endpoint = endpoint,
            };

        /// <summary>
        /// Reads a required object argument (<c>values</c>/<c>set</c>) into a
        /// case-insensitive column-value map. The pipeline (not the adapter) decides
        /// which columns are writable and pins tenant scope, so this only shapes JSON
        /// into CLR values — it builds no predicate and drops no server-enforced column.
        /// </summary>
        private static Dictionary<string, object?> ReadColumnObject(IDictionary<string, JsonElement>? args, string name)
        {
            var element = GetArgument(args, name)
                ?? throw new ToolPromptException($"Missing required argument '{name}' (a column-value object).");
            if (element.ValueKind != JsonValueKind.Object)
                throw new ToolPromptException($"Argument '{name}' must be an object of column values.");

            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                data[property.Name] = QueryToolCompiler.ToClrValue(property.Value);
            return data;
        }

        /// <summary>
        /// Parses the positional primary key from the <c>id</c> argument — an array in
        /// key-column order, a 'v1|v2' delimited string, or a scalar — into the ordered
        /// value list <see cref="MutationIntent.PrimaryKey"/> expects. Arity and column
        /// coercion are enforced downstream by the mutation pipeline (composite-key safe).
        /// </summary>
        private static IReadOnlyList<object?> ReadPrimaryKey(IDictionary<string, JsonElement>? args)
        {
            var element = GetArgument(args, "id")
                ?? throw new ToolPromptException("Missing required argument 'id' (the row's primary-key value).");
            return element.ValueKind switch
            {
                JsonValueKind.Array => element.EnumerateArray().Select(QueryToolCompiler.ToClrValue).ToList(),
                JsonValueKind.String when element.GetString()!.Contains('|') =>
                    element.GetString()!.Split('|').Select(s => (object?)s).ToList(),
                JsonValueKind.String or JsonValueKind.Number => new List<object?> { QueryToolCompiler.ToClrValue(element) },
                _ => throw new ToolPromptException(
                    "id must be a primary-key value: a scalar, an array in key-column order, or a 'v1|v2' delimited string."),
            };
        }
    }
}

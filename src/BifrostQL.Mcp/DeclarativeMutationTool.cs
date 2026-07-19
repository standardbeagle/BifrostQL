using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Resolvers;
using ModelContextProtocol.Protocol;
using static BifrostQL.Mcp.ToolJson;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Executes a declared WRITE tool (<see cref="DeclarativeToolDefinition.Mutation"/>).
    /// The tool maps its parameters and fixed/default literals onto a single
    /// <see cref="MutationIntent"/> routed EXCLUSIVELY through
    /// <see cref="IMutationIntentExecutor"/> — the full <c>TableMutationPipeline</c>
    /// (tenant scoping, audit actor, soft-delete, field encryption, history hooks). It
    /// renders no SQL, touches no <c>SqlExecutionManager</c>, and builds NO
    /// WHERE/predicate: scope narrowing comes only from the caller's identity via
    /// <see cref="MutationIntent.UserContext"/>, so an out-of-scope primary key affects
    /// zero rows (protocol-adapter-security invariant 7/8).
    ///
    /// <para>The whole declared-write surface is OFF by default; the enable gate is the
    /// FIRST check in the server's mutation-tool branch, so a disabled deployment builds
    /// zero intent. Destructive actions (update/delete) additionally require explicit
    /// confirmation before any intent is constructed.</para>
    /// </summary>
    internal static class DeclarativeMutationTool
    {
        internal const string ConfirmArgument = "confirm";

        private static readonly JsonElement WriteOutputSchema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "table": { "type": "string" },
                "action": { "type": "string", "enum": ["insert", "update", "delete"] },
                "result": { "description": "Insert: the generated identity. Update/Delete: rows affected within your access scope (0 when the row is outside it)." }
              },
              "required": ["table", "action", "result"]
            }
            """);

        internal static bool IsDestructive(DeclarativeToolMutation mutation) =>
            mutation.Action is "update" or "delete";

        public static Tool BuildTool(DeclarativeToolDefinition definition)
        {
            var mutation = definition.Mutation!;
            var destructive = IsDestructive(mutation);
            return new Tool
            {
                Name = definition.Name,
                Description = definition.Description,
                InputSchema = JsonSerializer.SerializeToElement(BuildInputSchema(definition, destructive)),
                OutputSchema = WriteOutputSchema,
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = false,
                    DestructiveHint = destructive,
                    IdempotentHint = mutation.Action is "update" or "delete",
                },
            };
        }

        public static async Task<JsonObject> ExecuteAsync(
            IMutationIntentExecutor mutationExecutor,
            DeclarativeToolDefinition definition,
            string? endpoint,
            IReadOnlyDictionary<string, JsonElement> arguments,
            IDictionary<string, object?> userContext,
            CancellationToken cancellationToken)
        {
            var mutation = definition.Mutation!;

            // Destructive actions require confirmation BEFORE any intent is built.
            if (IsDestructive(mutation) && !IsConfirmed(arguments))
                throw new ToolPromptException(
                    $"Tool '{definition.Name}' is destructive ({mutation.Action}). Re-invoke with \"{ConfirmArgument}\": true to proceed; " +
                    "no change was made.");

            var intent = BuildIntent(mutation, endpoint, definition, arguments, userContext);
            var result = await mutationExecutor.ExecuteAsync(intent, cancellationToken);

            // Insert reports the generated identity; update/delete report the real
            // affected-row count (never the pipeline's Value, which is the KEY for a
            // single-key update — invariant 8b).
            var resultValue = mutation.Action switch
            {
                "insert" => result.Value,
                "update" => (object?)(result.AffectedRows ?? 0),
                _ => result.Value,
            };
            return new JsonObject
            {
                ["table"] = mutation.Table,
                ["action"] = mutation.Action,
                ["result"] = ToJsonNode(resultValue),
            };
        }

        private static MutationIntent BuildIntent(
            DeclarativeToolMutation mutation,
            string? endpoint,
            DeclarativeToolDefinition definition,
            IReadOnlyDictionary<string, JsonElement> arguments,
            IDictionary<string, object?> userContext)
        {
            var action = mutation.Action switch
            {
                "insert" => MutationIntentAction.Insert,
                "update" => MutationIntentAction.Update,
                "delete" => MutationIntentAction.Delete,
                _ => throw new ToolPromptException($"Unknown mutation action '{mutation.Action}'."),
            };

            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in mutation.Values)
                data[column] = ResolveValue(definition.Name, column, value, arguments);

            IReadOnlyList<object?>? primaryKey = mutation.ById is { } byId
                ? ParsePrimaryKey(definition.Name, byId, arguments)
                : null;

            // Only column values + positional PK + caller context. No predicate: the
            // pipeline narrows scope from UserContext, so an out-of-scope key is a no-op.
            return new MutationIntent
            {
                // The DSL qualifies the table (schema.name) for validation parity with
                // read tools; the mutation pipeline resolves by bare DbName.
                Table = UnqualifyTable(mutation.Table),
                Action = action,
                Data = data,
                PrimaryKey = primaryKey,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };
        }

        /// <summary>
        /// Resolves a declared column value: a <c>$param</c> reference binds the
        /// call-time argument; any other JSON value is a fixed literal. A fixed literal
        /// for a security-pinned column is still overridden by the pipeline transformer.
        /// </summary>
        private static object? ResolveValue(
            string toolName, string column, JsonElement value, IReadOnlyDictionary<string, JsonElement> arguments)
        {
            if (DeclarativeToolDocumentValidator.TryParameterReference(value) is { } parameterName)
            {
                if (!arguments.TryGetValue(parameterName, out var argument))
                    throw new ToolPromptException(
                        $"Tool '{toolName}' requires parameter '{parameterName}' for column '{column}'.");
                return QueryToolCompiler.ToClrValue(argument);
            }
            return QueryToolCompiler.ToClrValue(value);
        }

        private static IReadOnlyList<object?> ParsePrimaryKey(
            string toolName, string byId, IReadOnlyDictionary<string, JsonElement> arguments)
        {
            if (!arguments.TryGetValue(byId, out var element))
                throw new ToolPromptException($"Tool '{toolName}' requires the primary-key parameter '{byId}'.");
            return element.ValueKind switch
            {
                JsonValueKind.Array => element.EnumerateArray().Select(QueryToolCompiler.ToClrValue).ToList(),
                JsonValueKind.String when element.GetString()!.Contains('|') =>
                    element.GetString()!.Split('|').Select(s => (object?)s).ToList(),
                JsonValueKind.String or JsonValueKind.Number => new List<object?> { QueryToolCompiler.ToClrValue(element) },
                _ => throw new ToolPromptException(
                    $"Parameter '{byId}' must be a primary-key value: a scalar, an array in key-column order, or a 'v1|v2' delimited string."),
            };
        }

        /// <summary>Strips a leading <c>schema.</c> qualifier to the bare table DbName the pipeline resolves by.</summary>
        private static string UnqualifyTable(string qualified)
        {
            var dot = qualified.LastIndexOf('.');
            return dot >= 0 ? qualified[(dot + 1)..] : qualified;
        }

        private static bool IsConfirmed(IReadOnlyDictionary<string, JsonElement> arguments) =>
            arguments.TryGetValue(ConfirmArgument, out var confirm)
            && confirm.ValueKind == JsonValueKind.True;

        private static JsonObject BuildInputSchema(DeclarativeToolDefinition definition, bool destructive)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var (name, parameter) in definition.Params)
            {
                properties[name] = ParameterSchema(parameter);
                if (parameter.Default is null)
                    required.Add(name);
            }
            if (destructive)
                properties[ConfirmArgument] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Must be true to perform this destructive write. Omitted or false makes no change.",
                };
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        private static JsonObject ParameterSchema(DeclarativeToolParameter parameter)
        {
            var schema = new JsonObject
            {
                ["type"] = parameter.Type switch
                {
                    "int" or "integer" => "integer",
                    "number" => "number",
                    "bool" or "boolean" => "boolean",
                    _ => "string",
                },
            };
            if (parameter.Values is { Count: > 0 })
                schema["enum"] = new JsonArray(parameter.Values.Select(value => JsonValue.Create(value)).ToArray());
            if (parameter.Description is not null)
                schema["description"] = parameter.Description;
            if (parameter.Default is { } value)
                schema["default"] = JsonNode.Parse(value.GetRawText());
            return schema;
        }
    }
}

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
            IQueryIntentExecutor executor,
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

            // The declared table's key ARITY decides whether a '|' in the id parameter separates
            // key values or is just a character of a single-column key (see ToolJson.ParseKeyValues).
            // A declared table is validated against the model at load; an unresolvable name keeps
            // the non-splitting reading and is rejected downstream by the pipeline — the split is
            // never invented for a table this could not resolve.
            var model = await executor.GetModelAsync(endpoint);
            var declaredTable = UnqualifyTable(mutation.Table);
            var keyColumnCount = model.Tables
                .FirstOrDefault(t => string.Equals(t.DbName, declaredTable, StringComparison.OrdinalIgnoreCase))
                ?.KeyColumns.Count() ?? 1;

            var intent = BuildIntent(mutation, endpoint, definition, arguments, userContext, keyColumnCount);
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
            IDictionary<string, object?> userContext,
            int keyColumnCount)
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
                data[column] = ResolveValue(definition, column, value, arguments);

            IReadOnlyList<object?>? primaryKey = mutation.ById is { } byId
                ? ParsePrimaryKey(definition.Name, byId, arguments, keyColumnCount)
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
        /// Binding honors the parameter declaration the input schema advertises
        /// (M22): an absent argument falls back to the declared <c>default</c>, and a
        /// present argument must satisfy the declared <c>type</c> and <c>enum</c> —
        /// a violation is a client-shape <see cref="ToolPromptException"/> thrown
        /// BEFORE any intent reaches the pipeline.
        /// </summary>
        private static object? ResolveValue(
            DeclarativeToolDefinition definition, string column, JsonElement value,
            IReadOnlyDictionary<string, JsonElement> arguments)
        {
            if (DeclarativeToolDocumentValidator.TryParameterReference(value) is { } parameterName)
            {
                definition.Params.TryGetValue(parameterName, out var parameter);
                if (!arguments.TryGetValue(parameterName, out var argument))
                {
                    if (parameter?.Default is { } declaredDefault)
                        return QueryToolCompiler.ToClrValue(declaredDefault);
                    throw new ToolPromptException(
                        $"Tool '{definition.Name}' requires parameter '{parameterName}' for column '{column}'.");
                }
                ValidateArgument(definition.Name, parameterName, argument, parameter);
                return QueryToolCompiler.ToClrValue(argument);
            }
            return QueryToolCompiler.ToClrValue(value);
        }

        /// <summary>
        /// Enforces at bind what <see cref="BuildInputSchema"/> advertises: enum
        /// membership and the JSON kind implied by the declared type. The schema is
        /// advisory to clients; this check is the enforcement. The message names only
        /// the parameter — never the offending value.
        /// </summary>
        private static void ValidateArgument(
            string toolName, string parameterName, JsonElement argument, DeclarativeToolParameter? parameter)
        {
            if (parameter is null)
                return;
            if (parameter.Values is { Count: > 0 } allowed &&
                (argument.ValueKind != JsonValueKind.String
                 || !allowed.Contains(argument.GetString(), StringComparer.Ordinal)))
                throw new ToolPromptException(
                    $"Parameter '{parameterName}' of tool '{toolName}' must be one of the declared values.");
            var typeMatches = parameter.Type switch
            {
                "int" or "integer" => argument.ValueKind == JsonValueKind.Number && argument.TryGetInt64(out _),
                "number" => argument.ValueKind == JsonValueKind.Number,
                "bool" or "boolean" => argument.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "string" => argument.ValueKind == JsonValueKind.String,
                _ => true, // "id" and unknown kinds are validated by their own consumers.
            };
            if (!typeMatches)
                throw new ToolPromptException(
                    $"Parameter '{parameterName}' of tool '{toolName}' must match its declared type.");
        }

        private static IReadOnlyList<object?> ParsePrimaryKey(
            string toolName, string byId, IReadOnlyDictionary<string, JsonElement> arguments, int keyColumnCount)
        {
            if (!arguments.TryGetValue(byId, out var element))
                throw new ToolPromptException($"Tool '{toolName}' requires the primary-key parameter '{byId}'.");
            return ToolJson.ParseKeyValues(element, keyColumnCount, byId);
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

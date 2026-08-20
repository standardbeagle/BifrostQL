using BifrostQL.Core.Auth;
using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Shared argument-parsing and JSON-shaping helpers for the MCP data tools.
    /// Argument mistakes throw <see cref="ToolPromptException"/> with an
    /// agent-actionable message; table resolution reuses the schema tools'
    /// did-you-mean prompt so every tool corrects a typo in one round trip.
    /// </summary>
    internal static class ToolJson
    {
        /// <summary>
        /// Resolves a caller-named table against the tables that caller may READ. The
        /// prompt-style failure lists only the VISIBLE tables, so a mistyped table name
        /// never enumerates the schema past the caller's authorization, and a
        /// read-denied table resolves exactly like a non-existent one — no oracle
        /// (protocol-adapter-security invariant 4).
        /// </summary>
        internal static IDbTable ResolveTable(
            IDbModel model, IDictionary<string, object?> userContext, string tableName)
        {
            // Resolution itself is against the FULL model on purpose: a policy-denied
            // table must still reach the transformer pipeline so the caller gets the
            // AUTHORITATIVE server-side rejection, the same condition every other
            // adapter raises for it (invariant 10 — one condition, one wire status
            // across adapters; the shared conformance kit pins this).
            var table = FindUnambiguous(model, tableName);
            if (table is not null)
                return table;

            // The FAILURE message, though, is an introspection surface: listing every
            // table in the model let any caller enumerate the schema by mistyping one
            // table name. It names only the tables this caller may READ (invariant 4).
            throw new ToolPromptException(SchemaDescriber.UnknownTableMessage(
                SchemaReadVisibility.Project(model, userContext), tableName));
        }

        internal static IDbTable ResolveTable(
            IDbModel model, IReadOnlyList<VisibleTable> visible, string tableName) =>
            FindUnambiguous(model, tableName)
            ?? throw new ToolPromptException(SchemaDescriber.UnknownTableMessage(visible, tableName));

        /// <summary>
        /// Bare-name lookup that never guesses between schemas. MCP tools address tables by
        /// bare name only, so a name two schemas both define cannot be resolved — picking
        /// the first match (the prior behavior) silently binds the caller's operation to an
        /// arbitrary schema's table, with THAT table's policy/tenant scope. Failing fast
        /// does reveal that the name is multiply defined, but the alternative is executing
        /// against a table the caller never addressed — the same trade
        /// <see cref="DbModel.GetTableFromDbName(string)"/> makes.
        /// </summary>
        private static IDbTable? FindUnambiguous(IDbModel model, string tableName)
        {
            IDbTable? found = null;
            foreach (var table in model.Tables)
            {
                if (!string.Equals(table.DbName, tableName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (found is not null)
                    throw new ToolPromptException(
                        $"Table name '{tableName}' is ambiguous: more than one schema defines a table " +
                        "with this name, and MCP tools address tables by bare name. Ask the operator " +
                        "to expose an unambiguous name for the table you need.");
                found = table;
            }
            return found;
        }

        /// <summary>
        /// Resolves a caller-named table to the caller's READABLE projection of it, or throws the
        /// same "unknown table" prompt a NON-EXISTENT table gets — so a read-denied table is
        /// indistinguishable from one that does not exist (invariant 4), exactly as
        /// <c>bifrost_describe_table</c> already answers. Unlike <see cref="ResolveTable(IDbModel,IDictionary{string,object?},string)"/>
        /// (which returns the raw table on purpose so a data operation reaches the pipeline for the
        /// authoritative rejection), the aggregate/search/row-context tools build column/key error
        /// PROMPTS off the resolved table BEFORE execution; resolving those through the visible
        /// projection keeps a denied table's column and key names — and its very existence — out of
        /// those prompts. Returns the <see cref="VisibleTable"/> so callers use its readable columns.
        /// </summary>
        internal static VisibleTable ResolveVisibleTable(
            IDbModel model, IReadOnlyList<VisibleTable> visible, string tableName)
        {
            // Same no-guessing rule as FindUnambiguous, over the caller's readable set:
            // two visible tables sharing the bare name cannot be told apart by an MCP
            // caller, so answer with the ambiguity rather than an arbitrary pick. Both
            // candidates are readable here, so the message discloses nothing hidden.
            VisibleTable? found = null;
            foreach (var candidate in visible)
            {
                if (!string.Equals(candidate.Table.DbName, tableName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (found is not null)
                    throw new ToolPromptException(
                        $"Table name '{tableName}' is ambiguous: more than one schema defines a table " +
                        "with this name, and MCP tools address tables by bare name. Ask the operator " +
                        "to expose an unambiguous name for the table you need.");
                found = candidate;
            }
            return found
                ?? throw new ToolPromptException(SchemaDescriber.UnknownTableMessage(visible, tableName));
        }

        /// <inheritdoc cref="ResolveVisibleTable(IDbModel,IReadOnlyList{VisibleTable},string)"/>
        internal static VisibleTable ResolveVisibleTable(
            IDbModel model, IDictionary<string, object?> userContext, string tableName) =>
            ResolveVisibleTable(model, SchemaReadVisibility.Project(model, userContext), tableName);

        internal static JsonArray ToJsonRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
            new(rows.Select(r => (JsonNode?)ToJsonRow(r)).ToArray());

        internal static JsonObject ToJsonRow(IReadOnlyDictionary<string, object?> row)
        {
            var obj = new JsonObject();
            foreach (var (column, value) in row)
                obj[column] = ToJsonNode(value);
            return obj;
        }

        internal static JsonNode? ToJsonNode(object? value) =>
            value is null ? null : JsonSerializer.SerializeToNode(value);

        internal static JsonElement? GetArgument(IDictionary<string, JsonElement>? args, string name) =>
            args is not null && args.TryGetValue(name, out var value) ? value : null;

        internal static string? GetStringArgument(IDictionary<string, JsonElement>? args, string name)
        {
            var element = GetArgument(args, name);
            if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (e.ValueKind != JsonValueKind.String)
                throw new ToolPromptException($"Argument '{name}' must be a string.");
            return e.GetString();
        }

        internal static string? GetString(JsonElement obj, string property)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new ToolPromptException($"'{property}' must be a string.");
            return value.GetString();
        }

        internal static IReadOnlyList<string>? GetStringArray(IDictionary<string, JsonElement>? args, string name)
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

        internal static JsonElement ParseSchema(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}

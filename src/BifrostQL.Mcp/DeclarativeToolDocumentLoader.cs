using System.Text.Json;
using System.Diagnostics;

namespace BifrostQL.Mcp
{
    public sealed class DeclarativeToolDocumentLoader(
        IDeclarativeToolDocumentSource source,
        Action<string>? warningLogger = null)
    {
        private static readonly HashSet<string> DocumentKeys = ["version", "tools"];
        private static readonly HashSet<string> ToolKeys = ["name", "description", "params", "root", "include", "policy"];
        private static readonly HashSet<string> ParameterKeys = ["type", "table", "description", "values", "default"];
        private static readonly HashSet<string> RootKeys = ["table", "byId", "fields"];
        private static readonly HashSet<string> IncludeKeys = ["relation", "as", "filter", "fields", "sort", "limit", "aggregate", "detailGate"];
        private static readonly HashSet<string> AggregateKeys = ["count", "sum", "avg", "min", "max"];
        private static readonly HashSet<string> PolicyKeys = ["hiddenFieldBehavior", "allowedRoles"];

        public DeclarativeToolDocument Load()
        {
            try
            {
                using var stream = source.OpenRead();
                using var json = JsonDocument.Parse(stream);
                ValidateShape(json.RootElement);
                return json.RootElement.Deserialize<DeclarativeToolDocument>(DeclarativeToolJson.Options)
                    ?? throw new InvalidOperationException("Tool document is empty.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Failed to load declarative MCP tool document from '{source.Description}': {ex.Message}", ex);
            }
        }

        private void ValidateShape(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Tool document root must be an object.");

            RejectUnknown(root, DocumentKeys, "tool document");
            if (!root.TryGetProperty("version", out var version))
                throw new JsonException("Tool document is missing required property 'version'.");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1)
                throw new JsonException($"Unsupported declarative MCP tool document version '{version}'. Expected version 1.");
            if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                throw new JsonException("Tool document property 'tools' must be an array.");

            foreach (var tool in tools.EnumerateArray())
                ValidateTool(tool);
        }

        private void ValidateTool(JsonElement tool)
        {
            if (tool.ValueKind != JsonValueKind.Object)
                throw new JsonException("Each entry in 'tools' must be an object.");
            var name = tool.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var label = string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name;

            RejectUnknown(tool, ToolKeys, $"tool '{label}'");
            RequireString(tool, "name", $"tool '{label}'");
            RequireString(tool, "description", $"tool '{label}'");
            var description = tool.GetProperty("description").GetString()!;
            if (description.Trim().Length < 10)
                throw new JsonException($"Tool '{label}' description must be at least 10 characters long.");
            RequireObject(tool, "params", $"tool '{label}'", required: false, out var parameters);
            if (parameters is { } parameterObject)
            {
                foreach (var parameter in parameterObject.EnumerateObject())
                {
                    if (parameter.Value.ValueKind != JsonValueKind.Object)
                        throw new JsonException($"Tool '{label}' parameter '{parameter.Name}' must be an object.");
                    RejectUnknown(parameter.Value, ParameterKeys, $"tool '{label}' parameter '{parameter.Name}'");
                    RequireString(parameter.Value, "type", $"tool '{label}' parameter '{parameter.Name}'");
                    if (!parameter.Value.TryGetProperty("description", out var parameterDescription) ||
                        parameterDescription.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(parameterDescription.GetString()))
                    {
                        var warning = $"Declarative MCP tool '{label}' parameter '{parameter.Name}' has no description.";
                        if (warningLogger is null) Trace.TraceWarning(warning);
                        else warningLogger(warning);
                    }
                }
            }

            RequireObject(tool, "root", $"tool '{label}'", required: true, out var root);
            RejectUnknown(root!.Value, RootKeys, $"tool '{label}' root");
            RequireString(root.Value, "table", $"tool '{label}' root");
            RequireString(root.Value, "byId", $"tool '{label}' root");
            RequireArray(root.Value, "fields", $"tool '{label}' root");

            if (tool.TryGetProperty("include", out var includes))
            {
                if (includes.ValueKind != JsonValueKind.Array)
                    throw new JsonException($"Tool '{label}' property 'include' must be an array.");
                foreach (var include in includes.EnumerateArray())
                {
                    if (include.ValueKind != JsonValueKind.Object)
                        throw new JsonException($"Tool '{label}' include entries must be objects.");
                    RejectUnknown(include, IncludeKeys, $"tool '{label}' include");
                    RequireString(include, "relation", $"tool '{label}' include");
                    RequireString(include, "as", $"tool '{label}' include");
                    if (include.TryGetProperty("aggregate", out var aggregate))
                    {
                        if (aggregate.ValueKind != JsonValueKind.Object)
                            throw new JsonException($"Tool '{label}' include property 'aggregate' must be an object.");
                        RejectUnknown(aggregate, AggregateKeys, $"tool '{label}' include aggregate");
                    }
                }
                if (includes.EnumerateArray().Any(include =>
                        include.TryGetProperty("detailGate", out var gate) && gate.GetString() == "full") &&
                    parameters is { } parameterSet && parameterSet.TryGetProperty("detail", out var detailParameter))
                {
                    var validValues = detailParameter.TryGetProperty("values", out var values) &&
                        values.ValueKind == JsonValueKind.Array &&
                        values.EnumerateArray().Select(value => value.GetString()).SequenceEqual(["summary", "full"]);
                    if (detailParameter.GetProperty("type").GetString() != "enum" || !validValues)
                        throw new JsonException($"Tool '{label}' parameter 'detail' is reserved for detail gating and must be enum [summary, full].");
                }
            }

            if (tool.TryGetProperty("policy", out var policy))
            {
                if (policy.ValueKind != JsonValueKind.Object)
                    throw new JsonException($"Tool '{label}' property 'policy' must be an object.");
                RejectUnknown(policy, PolicyKeys, $"tool '{label}' policy");
            }
        }

        private static void RejectUnknown(JsonElement value, HashSet<string> allowed, string location)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                    throw new JsonException($"Unknown property '{property.Name}' in {location}.");
            }
        }

        private static void RequireString(JsonElement value, string propertyName, string location)
        {
            if (!value.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
                throw new JsonException($"{location} requires a non-empty string property '{propertyName}'.");
        }

        private static void RequireArray(JsonElement value, string propertyName, string location)
        {
            if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
                throw new JsonException($"{location} requires array property '{propertyName}'.");
        }

        private static void RequireObject(
            JsonElement value,
            string propertyName,
            string location,
            bool required,
            out JsonElement? result)
        {
            result = null;
            if (!value.TryGetProperty(propertyName, out var property))
            {
                if (required)
                    throw new JsonException($"{location} requires object property '{propertyName}'.");
                return;
            }
            if (property.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{location} property '{propertyName}' must be an object.");
            result = property;
        }
    }
}

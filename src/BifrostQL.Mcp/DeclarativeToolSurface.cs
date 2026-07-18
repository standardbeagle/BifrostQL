using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace BifrostQL.Mcp;

public static class DeclarativeToolSurface
{
    public static Tool BuildTool(DeclarativeToolDefinition definition, IDbModel model)
    {
        // Compilation here makes tools/list fail before publishing an invalid surface.
        var root = ResolveTable(definition, model);
        return new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = JsonSerializer.SerializeToElement(BuildInputSchema(definition)),
            OutputSchema = JsonSerializer.SerializeToElement(BuildOutputSchema(definition, root)),
            Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
        };
    }

    public static async Task<JsonObject> ExecuteAsync(
        DeclarativeToolDefinition definition,
        IDbModel model,
        IQueryIntentExecutor executor,
        string? endpoint,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken)
    {
        var compiled = DeclarativeQueryToolCompiler.Compile(definition, model, executor, endpoint);
        var result = await compiled.ExecuteAsync(arguments, userContext, cancellationToken);
        var row = result.Rows.FirstOrDefault();
        JsonNode? data = row is null
            ? null
            : JsonSerializer.SerializeToNode(row, McpJsonUtilities.DefaultOptions);
        if (data is JsonObject dataObject)
        {
            var collections = await compiled.ExecuteCollectionIncludesAsync(arguments, userContext, cancellationToken);
            foreach (var (name, rows) in collections)
                dataObject[name] = JsonSerializer.SerializeToNode(rows, McpJsonUtilities.DefaultOptions);
        }
        return new JsonObject
        {
            ["found"] = row is not null,
            ["data"] = data,
        };
    }

    private static JsonObject BuildInputSchema(DeclarativeToolDefinition definition)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, parameter) in definition.Params)
        {
            var schema = ParameterSchema(parameter);
            properties[name] = schema;
            if (parameter.Default is null)
                required.Add(name);
        }
        if (definition.Include.Any(include => include.DetailGate == "full") && !properties.ContainsKey("detail"))
            properties["detail"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("summary", "full"), ["default"] = "summary",
                ["description"] = "Use full to include detail-gated relationships; summary is the token-dense default.",
            };
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = required, ["additionalProperties"] = false,
        };
    }

    private static JsonObject ParameterSchema(DeclarativeToolParameter parameter)
    {
        var schema = new JsonObject
        {
            ["type"] = parameter.Type switch { "int" or "integer" => "integer", "number" => "number", "bool" or "boolean" => "boolean", _ => "string" },
        };
        if (parameter.Values is { Count: > 0 })
            schema["enum"] = new JsonArray(parameter.Values.Select(value => JsonValue.Create(value)).ToArray());
        if (parameter.Description is not null)
            schema["description"] = parameter.Description;
        if (parameter.Default is { } value)
            schema["default"] = JsonNode.Parse(value.GetRawText());
        return schema;
    }

    private static JsonObject BuildOutputSchema(DeclarativeToolDefinition definition, IDbTable root)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in definition.Root.Fields)
        {
            var column = ResolveColumn(root, field);
            properties[column.DbName] = ColumnSchema(column);
            required.Add(column.DbName);
        }
        foreach (var include in definition.Include)
        {
            var related = ResolveRelated(root, include.Relation);
            if (include.Fields is not null)
            {
                var itemProperties = new JsonObject();
                var itemRequired = new JsonArray();
                foreach (var field in include.Fields)
                {
                    var column = ResolveColumn(related, field);
                    itemProperties[column.DbName] = ColumnSchema(column);
                    itemRequired.Add(column.DbName);
                }
                properties[include.As] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["properties"] = itemProperties,
                        ["required"] = itemRequired, ["additionalProperties"] = false,
                    },
                };
                if (include.DetailGate != "full") required.Add(include.As);
            }
            if (include.Aggregate is { } aggregate)
            {
                AddAggregate(properties, required, include, aggregate.Count, "count", "integer");
                AddAggregate(properties, required, include, aggregate.Sum is not null, "sum", "number");
                AddAggregate(properties, required, include, aggregate.Avg is not null, "avg", "number");
                AddAggregate(properties, required, include, aggregate.Min is not null, "min", "number");
                AddAggregate(properties, required, include, aggregate.Max is not null, "max", "number");
            }
        }
        var dataSchema = new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = required, ["additionalProperties"] = false,
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["found"] = new JsonObject { ["type"] = "boolean" },
                ["data"] = new JsonObject { ["anyOf"] = new JsonArray(dataSchema, new JsonObject { ["type"] = "null" }) },
            },
            ["required"] = new JsonArray("found", "data"),
            ["additionalProperties"] = false,
        };
    }

    private static void AddAggregate(JsonObject properties, JsonArray required, DeclarativeToolInclude include, bool enabled, string suffix, string type)
    {
        if (!enabled) return;
        var name = $"{include.As}_{suffix}";
        properties[name] = new JsonObject { ["type"] = new JsonArray(type, "null") };
        if (include.DetailGate != "full") required.Add(name);
    }

    private static JsonObject ColumnSchema(ColumnDto column)
    {
        var type = column.DataType.ToLowerInvariant();
        var jsonType = type.Contains("int") ? "integer"
            : type.Contains("decimal") || type.Contains("numeric") || type.Contains("float") || type.Contains("double") || type.Contains("real") || type.Contains("money") ? "number"
            : type.Contains("bool") || type == "bit" ? "boolean" : "string";
        return new JsonObject
        {
            ["type"] = column.IsNullable ? new JsonArray(jsonType, "null") : JsonValue.Create(jsonType),
        };
    }

    private static IDbTable ResolveTable(DeclarativeToolDefinition definition, IDbModel model) =>
        model.Tables.First(table => string.Equals($"{table.TableSchema}.{table.DbName}", definition.Root.Table, StringComparison.OrdinalIgnoreCase));

    private static ColumnDto ResolveColumn(IDbTable table, string field) =>
        table.ColumnLookup.TryGetValue(field, out var column) ? column : table.GraphQlLookup[field];

    private static IDbTable ResolveRelated(IDbTable root, string relation)
    {
        if (root.ManyToManyLinks.TryGetValue(relation, out var many)) return many.TargetTable;
        var link = root.SingleLinks.TryGetValue(relation, out var single) ? single : root.MultiLinks[relation];
        return ReferenceEquals(link.ParentTable, root) ? link.ChildTable : link.ParentTable;
    }
}

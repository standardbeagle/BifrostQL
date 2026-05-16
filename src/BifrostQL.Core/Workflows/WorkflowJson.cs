using System.Text.Json;
using System.Text.Json.Serialization;
using BifrostQL.Core.AppMetadata;

namespace BifrostQL.Core.Workflows;

/// <summary>
/// Stable camelCase JSON contract for workflow definitions.
/// </summary>
public static class WorkflowJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return JsonSerializer.Serialize(workflow, Options);
    }

    public static WorkflowDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var result = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options);
        return result ?? throw new JsonException("Workflow JSON deserialized to null.");
    }

    public static string SerializeOverlay(AppMetadataModel metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonSerializer.Serialize(metadata, Options);
    }

    public static AppMetadataModel DeserializeOverlay(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var result = JsonSerializer.Deserialize<AppMetadataModel>(json, Options);
        return result ?? throw new JsonException("Workflow overlay JSON deserialized to null.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

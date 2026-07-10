using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// Centralized JSON contract for saved objects — the same camelCase + enum-as-string
/// conventions the app-metadata overlay uses (<c>AppMetadataJson</c>), so both server
/// pipelines present an identical shape to the client. Also the single place that
/// validates a saved object before it is persisted.
/// </summary>
public static class SavedObjectJson
{
    /// <summary>Default cap on a definition's serialized size (256 KB). Guards against unbounded payloads.</summary>
    public const int DefaultMaxDefinitionBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize(SavedObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return JsonSerializer.Serialize(obj, Options);
    }

    public static string SerializeList(IReadOnlyList<SavedObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return JsonSerializer.Serialize(objects, Options);
    }

    /// <summary>
    /// Parses request JSON into a validated <see cref="SavedObject"/>. Throws
    /// <see cref="SavedObjectValidationException"/> on a malformed or invalid body
    /// (bad JSON, unknown type, empty id/name, oversized definition) so the HTTP
    /// layer can answer 400 rather than persisting garbage.
    /// </summary>
    public static SavedObject Deserialize(string json, int maxDefinitionBytes = DefaultMaxDefinitionBytes)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SavedObjectValidationException("Request body is empty.");

        SavedObject? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SavedObject>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new SavedObjectValidationException($"Malformed saved-object JSON: {ex.Message}");
        }

        if (parsed == null)
            throw new SavedObjectValidationException("Saved-object body deserialized to null.");

        return Validate(parsed, maxDefinitionBytes);
    }

    /// <summary>
    /// Enforces the saved-object invariants shared by every store and transport:
    /// non-empty id and name, a definition within the size cap, and a non-negative
    /// version. The <see cref="SavedObjectType"/> enum already rejects unknown type
    /// strings at deserialization. Returns the object unchanged when valid.
    /// </summary>
    public static SavedObject Validate(SavedObject obj, int maxDefinitionBytes = DefaultMaxDefinitionBytes)
    {
        ArgumentNullException.ThrowIfNull(obj);

        if (string.IsNullOrWhiteSpace(obj.Id))
            throw new SavedObjectValidationException("Saved object 'id' is required.");
        if (string.IsNullOrWhiteSpace(obj.Name))
            throw new SavedObjectValidationException("Saved object 'name' is required.");
        if (obj.Version < 0)
            throw new SavedObjectValidationException("Saved object 'version' must not be negative.");
        if (obj.Definition.ValueKind == JsonValueKind.Undefined)
            throw new SavedObjectValidationException("Saved object 'definition' is required.");

        var definitionBytes = System.Text.Encoding.UTF8.GetByteCount(obj.Definition.GetRawText());
        if (definitionBytes > maxDefinitionBytes)
            throw new SavedObjectValidationException(
                $"Saved object 'definition' is {definitionBytes} bytes, exceeding the {maxDefinitionBytes}-byte cap.");

        return obj;
    }

    /// <summary>
    /// Parses a trusted, already-persisted object (file contents / DB column JSON)
    /// without the request-level validation. Returns null on malformed JSON so one
    /// corrupt record cannot fail a whole list read.
    /// </summary>
    public static SavedObject? TryDeserializeStored(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SavedObject>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a definition payload from its stored raw text into a <see cref="JsonElement"/>.</summary>
    public static JsonElement ParseDefinition(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.Clone();
    }

    /// <summary>Parses a <see cref="SavedObjectType"/> from its camelCase wire name; throws on an unknown value.</summary>
    public static SavedObjectType ParseType(string value)
    {
        if (Enum.TryParse<SavedObjectType>(value, ignoreCase: true, out var type))
            return type;
        throw new SavedObjectValidationException(
            $"Unknown saved-object type '{value}'. Valid types: {string.Join(", ", Enum.GetNames<SavedObjectType>()).ToLowerInvariant()}.");
    }
}

/// <summary>Raised for a malformed or invalid saved-object request. The HTTP layer maps this to 400.</summary>
public sealed class SavedObjectValidationException : Exception
{
    public SavedObjectValidationException(string message) : base(message) { }
}

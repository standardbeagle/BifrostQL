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

    /// <summary>
    /// Cap on an id's length, in characters. The id is a key column of the DB store, and a key
    /// column is bounded on SQL Server and MySQL; the file store holds the same cap so an object
    /// saved on the desktop can move to a hosted store.
    /// </summary>
    public const int MaxIdLength = QueryModel.SqlColumnDefinition.KeyTextMaxLength;

    /// <summary>
    /// Default cap on how many objects one owner may hold (1000). Without it an authenticated
    /// caller could PUT fresh ids until the disk or table filled.
    /// </summary>
    public const int DefaultMaxObjectsPerOwner = 1000;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        // Names only: with integer values allowed, a body type of 999 deserialized to an
        // undefined enum value and was persisted under a directory ListAsync never enumerates.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
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
    /// non-empty id (at most <see cref="MaxIdLength"/> characters) and name, a definition within
    /// the size cap, and a non-negative
    /// version. The <see cref="SavedObjectType"/> enum already rejects unknown type
    /// strings at deserialization. Returns the object unchanged when valid.
    /// </summary>
    public static SavedObject Validate(SavedObject obj, int maxDefinitionBytes = DefaultMaxDefinitionBytes)
    {
        ArgumentNullException.ThrowIfNull(obj);

        if (!Enum.IsDefined(obj.Type))
            throw new SavedObjectValidationException("Saved object 'type' is not a known saved-object type.");
        if (string.IsNullOrWhiteSpace(obj.Id))
            throw new SavedObjectValidationException("Saved object 'id' is required.");
        if (obj.Id.Length > MaxIdLength)
            throw new SavedObjectValidationException(
                $"Saved object 'id' is {obj.Id.Length} characters, exceeding the {MaxIdLength}-character cap.");
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

    /// <summary>
    /// Parses a <see cref="SavedObjectType"/> from its camelCase wire name; throws on an unknown
    /// value. Only names are accepted: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// also accepts any numeric string ("999"), which is not a type.
    /// </summary>
    public static SavedObjectType ParseType(string value)
    {
        if (Enum.GetNames<SavedObjectType>().Contains(value, StringComparer.OrdinalIgnoreCase)
            && Enum.TryParse<SavedObjectType>(value, ignoreCase: true, out var type))
            return type;
        throw new SavedObjectValidationException(
            $"Unknown saved-object type '{value}'. Valid types: {string.Join(", ", Enum.GetNames<SavedObjectType>()).ToLowerInvariant()}.");
    }
}

/// <summary>
/// Raised when a create would take an owner past its object cap. The HTTP layer maps this to
/// 507 Insufficient Storage; updates to an existing object are never refused by the cap.
/// </summary>
public sealed class SavedObjectQuotaExceededException : Exception
{
    public SavedObjectQuotaExceededException(int maxObjectsPerOwner)
        : base($"The saved-object limit of {maxObjectsPerOwner} objects has been reached. Delete an object before creating another.")
    {
        MaxObjectsPerOwner = maxObjectsPerOwner;
    }

    public int MaxObjectsPerOwner { get; }
}

/// <summary>Raised for a malformed or invalid saved-object request. The HTTP layer maps this to 400.</summary>
public sealed class SavedObjectValidationException : Exception
{
    public SavedObjectValidationException(string message) : base(message) { }
}

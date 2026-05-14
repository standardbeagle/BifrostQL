using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// System.Text.Json serialization for the app-metadata overlay. Provides a
    /// stable, camelCase JSON contract intended for consumption by application
    /// clients (SPA and React Native). The contract uses only portable JSON
    /// primitives — no .NET-specific type names or formats — so it round-trips
    /// losslessly across platforms.
    /// </summary>
    public static class AppMetadataJson
    {
        /// <summary>
        /// The shared serializer options defining the stable overlay contract:
        /// camelCase property names, camelCase enum values (none currently,
        /// but reserved for forward compatibility), and null values omitted so
        /// absent optional metadata produces compact output.
        /// </summary>
        public static JsonSerializerOptions Options { get; } = CreateOptions();

        /// <summary>
        /// Serializes an <see cref="AppMetadataModel"/> aggregate to its stable
        /// camelCase JSON representation.
        /// </summary>
        public static string Serialize(AppMetadataModel metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            return JsonSerializer.Serialize(metadata, Options);
        }

        /// <summary>
        /// Deserializes the stable camelCase JSON representation back into an
        /// <see cref="AppMetadataModel"/> aggregate.
        /// </summary>
        /// <exception cref="JsonException">The JSON is malformed or does not
        /// represent an <see cref="AppMetadataModel"/> aggregate.</exception>
        public static AppMetadataModel Deserialize(string json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            var result = JsonSerializer.Deserialize<AppMetadataModel>(json, Options);
            return result ?? throw new JsonException(
                "App metadata JSON deserialized to null.");
        }

        /// <summary>
        /// Deserializes the stable camelCase JSON representation of a single
        /// <see cref="EntityMetadata"/> overlay entry. Used by sources that
        /// store one entity overlay per row (e.g. a database table keyed by
        /// qualified table name).
        /// </summary>
        /// <exception cref="JsonException">The JSON is malformed or does not
        /// represent an <see cref="EntityMetadata"/> entry.</exception>
        public static EntityMetadata DeserializeEntity(string json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            var result = JsonSerializer.Deserialize<EntityMetadata>(json, Options);
            return result ?? throw new JsonException(
                "App metadata entity JSON deserialized to null.");
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
}

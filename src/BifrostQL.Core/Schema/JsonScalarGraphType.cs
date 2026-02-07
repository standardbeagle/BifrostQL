using System.Text.Json;
using GraphQL.Types;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// A custom GraphQL scalar type for JSON values.
    /// Serializes parsed JSON objects in query results and accepts raw JSON objects as mutation input.
    /// </summary>
    public sealed class JsonScalarGraphType : ScalarGraphType
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        };

        public JsonScalarGraphType()
        {
            Name = "JSON";
            Description = "The JSON scalar type represents arbitrary JSON values.";
        }

        /// <summary>
        /// Called when returning data in query results.
        /// Converts JSON strings from the database into parsed JSON objects.
        /// </summary>
        public override object? Serialize(object? value)
        {
            return value switch
            {
                null => null,
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => DeserializeJson(s),
                JsonElement element => ConvertJsonElement(element),
                _ => value,
            };
        }

        /// <summary>
        /// Called when receiving mutation input variables.
        /// Converts JSON objects/arrays to a JSON string for database storage.
        /// </summary>
        public override object? ParseValue(object? value)
        {
            return value switch
            {
                null => null,
                string s => s,
                JsonElement element => element.GetRawText(),
                _ => JsonSerializer.Serialize(value, SerializerOptions),
            };
        }

        /// <summary>
        /// Called when parsing inline GraphQL literals.
        /// </summary>
        public override object? ParseLiteral(GraphQLValue value)
        {
            return value switch
            {
                GraphQLNullValue => null,
                GraphQLStringValue sv => sv.Value.ToString(),
                _ => throw new InvalidOperationException(
                    $"Cannot parse GraphQL literal of type {value.GetType().Name} as JSON. Use a JSON string or pass JSON via variables.")
            };
        }

        private static object? DeserializeJson(string json)
        {
            try
            {
                var element = JsonSerializer.Deserialize<JsonElement>(json);
                return ConvertJsonElement(element);
            }
            catch (JsonException)
            {
                return json;
            }
        }

        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => ConvertJsonObject(element),
                JsonValueKind.Array => ConvertJsonArray(element),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => element.GetRawText(),
            };
        }

        private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                dict[property.Name] = ConvertJsonElement(property.Value);
            }
            return dict;
        }

        private static List<object?> ConvertJsonArray(JsonElement element)
        {
            var list = new List<object?>();
            foreach (var item in element.EnumerateArray())
            {
                list.Add(ConvertJsonElement(item));
            }
            return list;
        }
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphQL.Types;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// A custom GraphQL scalar type for JSON values.
    /// Serializes parsed JSON objects in query results and accepts raw JSON objects as mutation input.
    ///
    /// <para>
    /// Numbers keep their exact text both ways. A stored number that fits a
    /// <see cref="long"/> is returned as one; any other number (fractional, or wider
    /// than 64 bits) is returned as its own <see cref="JsonElement"/>, which the
    /// System.Text.Json response serializer writes back verbatim. Parsing it to a
    /// double instead rounded 12345678901234567890123 to 1.2345678901234568E+22
    /// before any client saw it. On input, a wide integer the variable deserializer
    /// produced as a <see cref="BigInteger"/> is written as a JSON number, not as
    /// the struct's properties.
    /// </para>
    /// </summary>
    public sealed class JsonScalarGraphType : ScalarGraphType
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            Converters = { new BigIntegerNumberConverter() },
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
                // Cloned so the returned number elements outlive the caller's document.
                JsonElement element => ConvertJsonElement(element.Clone()),
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
                // Deserialize<JsonElement> owns its document, so the number elements
                // returned below stay valid after this method returns.
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
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element,
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

        /// <summary>
        /// Writes a <see cref="BigInteger"/> as a JSON number. System.Text.Json has no
        /// built-in mapping and would otherwise write its public properties
        /// (<c>{"Sign":1,"IsOne":false,…}</c>) into the stored document.
        /// </summary>
        private sealed class BigIntegerNumberConverter : JsonConverter<BigInteger>
        {
            public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => throw new NotSupportedException("JSON scalar input is only ever serialized, never read into a BigInteger.");

            public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
                => writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture));
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

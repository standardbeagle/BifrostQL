using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.OperationId
{
    /// <summary>
    /// What one write with an operation id is bound to: the table, the verb, the key it
    /// addresses and a hash of the arguments the client sent. A record is replayed only for the
    /// same fingerprint, so these fields are the identity of "the same request".
    /// </summary>
    internal sealed record OperationFingerprint(string Table, string Action, string TargetKey, string InputHash)
    {
        /// <summary>
        /// Builds the fingerprint of one write. <paramref name="arguments"/> are the field's
        /// client-supplied write arguments by GraphQL argument name (the verb's input object,
        /// <c>_primaryKey</c>, module arguments) and never the operation id itself; the hash
        /// covers every one of them, so two requests that differ in any argument value differ.
        /// <paramref name="targetKey"/> is the key the write addresses, by column name.
        /// </summary>
        public static OperationFingerprint Of(
            IDbTable table, string action,
            IReadOnlyDictionary<string, object?> targetKey,
            IReadOnlyDictionary<string, object?> arguments)
        {
            var qualified = $"{table.TableSchema}.{table.DbName}";
            var canonicalInput = CanonicalJson.Serialize(new Dictionary<string, object?>
            {
                ["table"] = qualified,
                ["action"] = action,
                ["arguments"] = arguments,
            });
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalInput))).ToLowerInvariant();
            return new OperationFingerprint(qualified, action, CanonicalJson.Serialize(targetKey), hash);
        }
    }

    /// <summary>
    /// A deterministic JSON form of argument values: object members sorted by ordinal name,
    /// lists in order, scalars as System.Text.Json writes them. The same values always produce
    /// the same text, whatever order the client wrote the members in.
    /// </summary>
    internal static class CanonicalJson
    {
        public static string Serialize(object? value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Write(writer, value);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void Write(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case string s:
                    writer.WriteStringValue(s);
                    return;
                case IDictionary dictionary:
                    writer.WriteStartObject();
                    foreach (var key in dictionary.Keys.Cast<object>()
                                 .Select(k => Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture)!)
                                 .OrderBy(k => k, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(key);
                        Write(writer, dictionary[key]);
                    }
                    writer.WriteEndObject();
                    return;
                case IEnumerable<KeyValuePair<string, object?>> pairs:
                    Write(writer, pairs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
                    return;
                case byte[] bytes:
                    writer.WriteBase64StringValue(bytes);
                    return;
                case IEnumerable list:
                    writer.WriteStartArray();
                    foreach (var item in list)
                        Write(writer, item);
                    writer.WriteEndArray();
                    return;
                default:
                    JsonSerializer.Serialize(writer, value, value.GetType());
                    return;
            }
        }
    }
}

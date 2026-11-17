using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphQL;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Reads GraphQL request variables (<see cref="Inputs"/>) like GraphQL.SystemTextJson's
/// <c>InputsJsonConverter</c>, except that a number neither <see cref="decimal"/> nor
/// <see cref="double"/> can hold exactly is kept as its own JSON number token.
///
/// <para>
/// The stock converter reads every non-integer number as a decimal or a double. A
/// <c>JSON</c> column value is a variable, so a number inside it past decimal's 28–29
/// significant digits (<c>0.1234567890123456789012345678901234567890</c>) was stored
/// rounded before the <c>JSON</c> scalar ever saw it. This converter returns such a
/// number as a <see cref="JsonElement"/> holding the token's raw text, which the
/// <c>JSON</c> scalar writes back verbatim, and which the <c>Decimal</c> and
/// <c>Float</c> scalars parse from that text (<see cref="ExactDecimalGraphType"/>,
/// <see cref="JsonNumberFloatGraphType"/>).
/// </para>
///
/// <para>
/// Every number either type does hold exactly keeps the stock converter's CLR value
/// (int, long, BigInteger, double or decimal, chosen the same way), so scalars and
/// resolvers see no change for them. Rejected: returning every fraction as a
/// <see cref="JsonElement"/> (every consumer of a plain <c>0.5</c> would change) and
/// parsing the text into a wider numeric type (none exists in the BCL, and the
/// <c>JSON</c> scalar only needs the text).
/// </para>
/// </summary>
public sealed class ExactNumberInputsJsonConverter : JsonConverter<Inputs>
{
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Inputs value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (IReadOnlyDictionary<string, object?>)value, options);

    /// <inheritdoc />
    public override Inputs Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadDictionary(ref reader).ToInputs();

    private static Dictionary<string, object?> ReadDictionary(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();
        var dictionary = new Dictionary<string, object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();
            var key = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException();
            dictionary.Add(key, ReadValue(ref reader));
        }
        return dictionary;
    }

    private static List<object?> ReadArray(ref Utf8JsonReader reader)
    {
        var list = new List<object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            list.Add(ReadValue(ref reader));
        return list;
    }

    private static object? ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.StartArray => ReadArray(ref reader),
        JsonTokenType.StartObject => ReadDictionary(ref reader),
        JsonTokenType.Number => ReadNumber(ref reader),
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Null or JsonTokenType.None => null,
        _ => throw new JsonException($"Unexpected token type: {reader.TokenType}"),
    };

    internal static object ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt32(out var i))
            return i;
        if (reader.TryGetInt64(out var l))
            return l;

        var raw = reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence)
            : Encoding.UTF8.GetString(reader.ValueSpan);
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
            return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var hasDouble = reader.TryGetDouble(out var d) && double.IsFinite(d);
        var hasDecimal = reader.TryGetDecimal(out var m);
        var token = JsonNumberText.Normalize(raw);

        if (hasDecimal && token == JsonNumberText.Normalize(m.ToString(CultureInfo.InvariantCulture)))
        {
            // The stock converter's choice between two exact readings: the double
            // when it carries the identical decimal (scale included), else the decimal.
            return hasDouble && decimal.GetBits(new decimal(d)).AsSpan().SequenceEqual(decimal.GetBits(m)) ? d : m;
        }
        if (hasDouble && token == JsonNumberText.Normalize(d.ToString("R", CultureInfo.InvariantCulture)))
            return d;

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Value comparison of JSON number texts: <c>1.50</c>, <c>15e-1</c> and
/// <c>1.5E+00</c> are the same number. A text whose exponent does not fit a long
/// normalizes to null, so it never equals a CLR reading.
/// </summary>
internal static class JsonNumberText
{
    public static (bool Negative, string Digits, long Exponent)? Normalize(string text)
    {
        var span = text.AsSpan().Trim();
        var negative = false;
        if (span.Length > 0 && (span[0] == '-' || span[0] == '+'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        long exponent = 0;
        var e = span.IndexOfAny('e', 'E');
        if (e >= 0)
        {
            if (!long.TryParse(span[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                return null;
            span = span[..e];
        }

        var dot = span.IndexOf('.');
        var digits = dot < 0 ? span.ToString() : string.Concat(span[..dot], span[(dot + 1)..]);
        if (dot >= 0)
            exponent -= span.Length - dot - 1;

        digits = digits.TrimStart('0');
        if (digits.Length == 0)
            return (false, "0", 0);
        var trimmed = digits.TrimEnd('0');
        exponent += digits.Length - trimmed.Length;
        return (negative, trimmed, exponent);
    }
}

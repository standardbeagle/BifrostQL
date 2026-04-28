using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BifrostQL.Core.Serialization;

/// <summary>
/// Deserializes PHP serialize() format strings into .NET objects.
/// Returns the raw string unchanged if parsing fails (graceful fallback).
/// </summary>
public static class PhpSerializer
{
    /// <summary>
    /// Attempts to deserialize a PHP serialized string.
    /// Returns a .NET object: string, long, double, bool, null, Dictionary&lt;string, object?&gt;, or List&lt;object?&gt;.
    /// Returns the original string if it doesn't appear to be PHP serialized or parsing fails.
    /// </summary>
    public static object? Deserialize(string? input)
    {
        if (input is null) return null;
        if (!IsPhpSerialized(input)) return input;

        try
        {
            var pos = 0;
            var result = ParseValue(input, ref pos);
            return result;
        }
        catch
        {
            return input;
        }
    }

    /// <summary>
    /// Converts a deserialized PHP value to a JSON string.
    /// Returns the original string if deserialization fails.
    /// </summary>
    public static string ToJson(string? input)
    {
        if (input is null) return "null";
        if (!IsPhpSerialized(input)) return input;

        try
        {
            var pos = 0;
            var result = ParseValue(input, ref pos);
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            return input;
        }
    }

    /// <summary>
    /// Checks if a string looks like PHP serialized data.
    /// </summary>
    public static bool IsPhpSerialized(string? input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        return input[0] switch
        {
            'N' => input.Length >= 2 && input[1] == ';',
            's' or 'i' or 'd' or 'b' or 'a' or 'O' => input.Length >= 2 && input[1] == ':',
            _ => false,
        };
    }

    private static object? ParseValue(string input, ref int pos)
    {
        if (pos >= input.Length) throw new FormatException("Unexpected end of input");

        return input[pos] switch
        {
            'N' => ParseNull(input, ref pos),
            'b' => ParseBool(input, ref pos),
            'i' => ParseInt(input, ref pos),
            'd' => ParseDouble(input, ref pos),
            's' => ParseString(input, ref pos),
            'a' => ParseArray(input, ref pos),
            'O' => ParseObject(input, ref pos),
            _ => throw new FormatException($"Unknown type '{input[pos]}' at position {pos}"),
        };
    }

    private static object? ParseNull(string input, ref int pos)
    {
        Expect(input, ref pos, 'N');
        Expect(input, ref pos, ';');
        return null;
    }

    private static bool ParseBool(string input, ref int pos)
    {
        Expect(input, ref pos, 'b');
        Expect(input, ref pos, ':');
        var val = input[pos];
        pos++;
        Expect(input, ref pos, ';');
        return val == '1';
    }

    private static long ParseInt(string input, ref int pos)
    {
        Expect(input, ref pos, 'i');
        Expect(input, ref pos, ':');
        var end = input.IndexOf(';', pos);
        if (end < 0) throw new FormatException("Missing ';' for integer");
        var val = long.Parse(input.AsSpan(pos, end - pos), CultureInfo.InvariantCulture);
        pos = end + 1;
        return val;
    }

    private static double ParseDouble(string input, ref int pos)
    {
        Expect(input, ref pos, 'd');
        Expect(input, ref pos, ':');
        var end = input.IndexOf(';', pos);
        if (end < 0) throw new FormatException("Missing ';' for double");
        var val = double.Parse(input.AsSpan(pos, end - pos), CultureInfo.InvariantCulture);
        pos = end + 1;
        return val;
    }

    private static string ParseString(string input, ref int pos)
    {
        Expect(input, ref pos, 's');
        Expect(input, ref pos, ':');
        var length = ParseLength(input, ref pos);
        Expect(input, ref pos, '"');
        // PHP serialize uses byte lengths, but we work with chars.
        // For pure ASCII this is identical. For multi-byte, we need byte-aware extraction.
        var str = ExtractByByteLength(input, ref pos, length);
        Expect(input, ref pos, '"');
        Expect(input, ref pos, ';');
        return str;
    }

    private static string ExtractByByteLength(string input, ref int pos, int byteLength)
    {
        // Fast path: if remaining chars are all ASCII, byte length == char length
        var remaining = input.Length - pos;
        if (remaining < byteLength) throw new FormatException("String extends beyond input");

        // Try char-by-char, counting UTF-8 bytes
        var sb = new StringBuilder();
        var bytesConsumed = 0;
        while (bytesConsumed < byteLength && pos < input.Length)
        {
            var ch = input[pos];
            var charBytes = Encoding.UTF8.GetByteCount(input, pos, char.IsHighSurrogate(ch) && pos + 1 < input.Length ? 2 : 1);
            if (bytesConsumed + charBytes > byteLength) break;
            if (char.IsHighSurrogate(ch) && pos + 1 < input.Length)
            {
                sb.Append(ch);
                sb.Append(input[pos + 1]);
                pos += 2;
            }
            else
            {
                sb.Append(ch);
                pos++;
            }
            bytesConsumed += charBytes;
        }
        return sb.ToString();
    }

    private static object? ParseArray(string input, ref int pos)
    {
        Expect(input, ref pos, 'a');
        Expect(input, ref pos, ':');
        var count = ParseLength(input, ref pos);
        Expect(input, ref pos, '{');

        var entries = new List<(object key, object? value)>(count);
        var allSequentialInt = true;

        for (var i = 0; i < count; i++)
        {
            var key = ParseValue(input, ref pos);
            var value = ParseValue(input, ref pos);
            entries.Add((key!, value));

            if (allSequentialInt && !(key is long idx && idx == i))
                allSequentialInt = false;
        }

        Expect(input, ref pos, '}');

        if (allSequentialInt)
        {
            var list = new List<object?>(count);
            foreach (var (_, value) in entries)
                list.Add(value);
            return list;
        }

        var dict = new Dictionary<string, object?>(count);
        foreach (var (key, value) in entries)
            dict[key.ToString()!] = value;
        return dict;
    }

    private static Dictionary<string, object?> ParseObject(string input, ref int pos)
    {
        Expect(input, ref pos, 'O');
        Expect(input, ref pos, ':');
        // Skip class name
        var nameLength = ParseLength(input, ref pos);
        Expect(input, ref pos, '"');
        pos += nameLength; // skip class name chars
        Expect(input, ref pos, '"');
        Expect(input, ref pos, ':');
        var count = ParseLength(input, ref pos);
        Expect(input, ref pos, '{');

        var dict = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ParseValue(input, ref pos);
            var value = ParseValue(input, ref pos);
            dict[key!.ToString()!] = value;
        }

        Expect(input, ref pos, '}');
        return dict;
    }

    private static int ParseLength(string input, ref int pos)
    {
        var end = input.IndexOf(':', pos);
        if (end < 0) throw new FormatException("Missing ':' for length");
        var length = int.Parse(input.AsSpan(pos, end - pos), CultureInfo.InvariantCulture);
        pos = end + 1;
        return length;
    }

    private static void Expect(string input, ref int pos, char expected)
    {
        if (pos >= input.Length || input[pos] != expected)
            throw new FormatException($"Expected '{expected}' at position {pos}, got '{(pos < input.Length ? input[pos] : "EOF")}'");
        pos++;
    }
}

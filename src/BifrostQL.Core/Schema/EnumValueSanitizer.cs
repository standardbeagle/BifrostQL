using System.Text.RegularExpressions;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Sanitizes database values into valid GraphQL enum value names.
///
/// Rules applied:
///   1. Convert to uppercase
///   2. Replace spaces and hyphens with underscores
///   3. Remove characters that are not alphanumeric or underscore
///   4. Collapse consecutive underscores
///   5. Trim leading/trailing underscores
///   6. Prefix with underscore if the result starts with a digit
///   7. Return null for empty results (value cannot be represented)
/// </summary>
public static class EnumValueSanitizer
{
    private static readonly Regex InvalidCharsPattern = new(@"[^A-Z0-9_]", RegexOptions.Compiled);
    private static readonly Regex ConsecutiveUnderscores = new(@"_{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a single database value into a valid GraphQL enum name.
    /// Returns null if the value cannot be represented as a valid enum name.
    /// </summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var result = value.Trim().ToUpperInvariant();
        result = result.Replace(' ', '_').Replace('-', '_');
        result = InvalidCharsPattern.Replace(result, "");
        result = ConsecutiveUnderscores.Replace(result, "_");
        result = result.Trim('_');

        if (string.IsNullOrEmpty(result))
            return null;

        if (char.IsDigit(result[0]))
            result = $"_{result}";

        return result;
    }

    /// <summary>
    /// Sanitizes a collection of database values, filtering out any that cannot be converted.
    /// Duplicate sanitized names are also removed (first occurrence wins).
    /// </summary>
    public static IReadOnlyList<EnumValueEntry> SanitizeAll(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<EnumValueEntry>();

        foreach (var value in values)
        {
            var sanitized = Sanitize(value);
            if (sanitized == null)
                continue;

            if (!seen.Add(sanitized))
                continue;

            results.Add(new EnumValueEntry(sanitized, value!));
        }

        return results;
    }
}

/// <summary>
/// Represents a sanitized enum value with its original database value.
/// </summary>
public sealed record EnumValueEntry(string GraphQlName, string DatabaseValue);

using System.Globalization;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Validation;

/// <summary>
/// The schema-derived value checks shared by every server surface that
/// validates a column value — the mutation transformer chain
/// (<see cref="ExtendedServerValidationTransformer"/>, covering GraphQL and all
/// protocol adapters) and the server-rendered form validator
/// (<c>BifrostFormValidator</c>). One implementation, so a value refused on one
/// surface can never pass another: anything the engine would reject on type
/// grounds (unparseable datetime, integer overflow, decimal precision overflow,
/// oversized binary) is refused with a clear per-field message before SQL runs.
/// </summary>
public static class SchemaDerivedValueValidator
{
    /// <summary>
    /// Runs every schema-derived check for one column value, appending messages
    /// prefixed with <paramref name="label"/> (the surface's field label — the
    /// GraphQL name in the pipeline, the form label in server-rendered forms).
    /// </summary>
    public static void Validate(
        string label, ColumnDto column, ValidationRules rules, object? value,
        ITypeMapper typeMapper, List<string> errors)
    {
        ValidateTemporal(label, column, rules, value, typeMapper, errors);
        ValidateIntegerRange(label, column, value, typeMapper, errors);
        ValidateDecimalPrecision(label, rules, value, errors);
        ValidateBinaryLength(label, rules, value, errors);
    }

    /// <summary>
    /// A temporal column's mutation input is a String on the wire (see
    /// ITypeMapper.GetGraphQlInsertTypeName), so nothing upstream has proven it
    /// parses. Refuse unparseable text, then refuse values outside the engine's
    /// storable range for the type (SQL Server datetime's 1753 floor, MySQL
    /// timestamp's 2038 ceiling) via the dialect's type mapper.
    /// </summary>
    private static void ValidateTemporal(
        string label, ColumnDto column, ValidationRules rules, object? value,
        ITypeMapper typeMapper, List<string> errors)
    {
        if (rules.TemporalKind == TemporalKind.None)
            return;

        DateTime? comparable = null;
        if (value is string text)
        {
            var (parsed, ok) = ParseTemporal(rules.TemporalKind, text);
            if (!ok)
            {
                errors.Add($"{label} must be a valid {TemporalNoun(rules.TemporalKind)} (ISO 8601).");
                return;
            }
            comparable = parsed;
        }
        else if (TryDate(value, out var dateValue))
        {
            comparable = dateValue;
        }

        if (comparable is null || rules.TemporalKind == TemporalKind.TimeOnly)
            return;

        if (typeMapper.GetTemporalRange(column.EffectiveDataType) is { } range
            && (comparable < range.Min || comparable > range.Max))
            errors.Add($"{label} must be between {range.Min:yyyy-MM-dd} and {range.Max:yyyy-MM-dd}.");
    }

    private static (DateTime? Value, bool Ok) ParseTemporal(TemporalKind kind, string text)
    {
        switch (kind)
        {
            case TemporalKind.DateTimeOffset:
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                    ? (dto.UtcDateTime, true)
                    : (null, false);
            case TemporalKind.DateOnly:
                // A full ISO datetime is accepted for a date column (engines cast it);
                // DateTime.TryParse covers both the bare date and the datetime forms.
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? (d.Date, true)
                    : (null, false);
            case TemporalKind.TimeOnly:
                return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out _)
                       || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out _)
                    ? (null, true)
                    : (null, false);
            default:
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? (dt, true)
                    : (null, false);
        }
    }

    private static string TemporalNoun(TemporalKind kind) => kind switch
    {
        TemporalKind.DateOnly => "date",
        TemporalKind.TimeOnly => "time",
        _ => "date/time",
    };

    /// <summary>
    /// Refuses values outside the engine's storable range for the column's
    /// integer type, and non-integral values on integer columns. GraphQL scalar
    /// coercion already bounds the GraphQL path; protocol adapters (RESP, MCP,
    /// pgwire) and form posts reach validation without it, so the check must
    /// live here to hold for every access method.
    /// </summary>
    private static void ValidateIntegerRange(
        string label, ColumnDto column, object? value, ITypeMapper typeMapper, List<string> errors)
    {
        if (typeMapper.GetIntegerRange(column.EffectiveDataType) is not { } range)
            return;
        if (value is bool)
            return;
        if (!TryDecimal(value, out var numeric))
        {
            // A non-numeric value on an integer column can only fail downstream.
            if (value is string)
                errors.Add($"{label} must be a whole number.");
            return;
        }

        if (decimal.Truncate(numeric) != numeric)
            errors.Add($"{label} must be a whole number.");
        else if (numeric < range.Min || numeric > range.Max)
            errors.Add($"{label} must be between {range.Min} and {range.Max}.");
    }

    /// <summary>
    /// Refuses a value whose integer part cannot fit the column's declared
    /// precision/scale — the overflow every engine rejects. Excess FRACTIONAL
    /// digits are deliberately allowed: engines round scale on write, and
    /// refusing what the engine accepts would break existing clients.
    /// </summary>
    private static void ValidateDecimalPrecision(
        string label, ValidationRules rules, object? value, List<string> errors)
    {
        if (rules.NumericPrecision is not { } precision)
            return;
        if (!TryDecimal(value, out var numeric))
            return;

        var scale = rules.NumericScale ?? 0;
        var integerDigits = Math.Max(precision - scale, 0);
        var limit = IntegerPartLimit(integerDigits);
        var integerPart = Math.Abs(decimal.Truncate(numeric));
        if (integerPart >= limit)
            errors.Add(
                $"{label} must have at most {integerDigits} digits before the decimal point.");
    }

    // 10^digits as a decimal without Math.Pow's double rounding; digits is at
    // most precision (<= 38), and 10^29 already exceeds decimal.MaxValue, at
    // which point no representable value can overflow the column.
    private static decimal IntegerPartLimit(int digits)
    {
        if (digits >= 29)
            return decimal.MaxValue;
        var limit = 1m;
        for (var i = 0; i < digits; i++)
            limit *= 10m;
        return limit;
    }

    /// <summary>
    /// Refuses binary payloads longer than the column's declared byte length.
    /// Raw bytes are measured directly; a string is measured by its decoded
    /// base64 length when it parses as base64 (the wire form clients send),
    /// otherwise left for the engine to arbitrate.
    /// </summary>
    private static void ValidateBinaryLength(
        string label, ValidationRules rules, object? value, List<string> errors)
    {
        if (rules.BinaryMaxLength is not { } maxBytes)
            return;

        int? byteLength = value switch
        {
            byte[] bytes => bytes.Length,
            string text when TryBase64Length(text, out var decoded) => decoded,
            _ => null,
        };
        if (byteLength > maxBytes)
            errors.Add($"{label} must be at most {maxBytes} bytes.");
    }

    private static bool TryBase64Length(string text, out int decodedLength)
    {
        var buffer = new byte[(text.Length / 4 + 1) * 3];
        if (Convert.TryFromBase64String(text, buffer, out decodedLength))
            return true;
        decodedLength = 0;
        return false;
    }

    internal static bool TryDate(object? raw, out DateTime value)
    {
        switch (raw)
        {
            case DateTime dt:
                value = dt;
                return true;
            case DateTimeOffset dto:
                value = dto.UtcDateTime;
                return true;
            case DateOnly d:
                value = d.ToDateTime(TimeOnly.MinValue);
                return true;
            default:
                value = default;
                return false;
        }
    }

    internal static bool TryDecimal(object? raw, out decimal value)
    {
        if (raw == null || raw is DBNull)
        {
            value = 0;
            return false;
        }

        return decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}

using System.Globalization;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Validation;

/// <summary>
/// The effective declarative validation rules for a column — the single
/// derivation shared by server enforcement
/// (<see cref="ExtendedServerValidationTransformer"/>), client exposure
/// (the <c>_dbSchema</c> meta query) and server-rendered forms, so a rule
/// declared once in schema metadata is enforced and advertised identically
/// everywhere.
///
/// Sources, in precedence order:
///   1. Validation metadata keys (<c>min</c>, <c>max</c>, <c>step</c>,
///      <c>minlength</c>, <c>maxlength</c>, <c>pattern</c>, <c>required</c>, …)
///   2. The database schema itself — varchar length becomes
///      <see cref="MaxLength"/>; NOT NULL (excluding identity/auto-populated
///      columns) becomes <see cref="RequiredImplied"/>.
///
/// <see cref="Min"/>/<see cref="Max"/> are kept as raw strings because they
/// may be numeric ("0.01") or temporal ("2020-01-01") depending on the column.
/// </summary>
public sealed record ValidationRules
{
    public string? Min { get; init; }
    public string? Max { get; init; }
    public string? Step { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Pattern { get; init; }
    public string? PatternMessage { get; init; }
    public string? InputType { get; init; }

    /// <summary>Required explicitly via the <c>required</c> metadata key. Drives server enforcement.</summary>
    public bool RequiredExplicit { get; init; }

    /// <summary>
    /// Required because the column is NOT NULL and the value cannot come from
    /// elsewhere (not identity, not auto-populated). Drives client UI hints;
    /// the database itself enforces it server-side.
    /// </summary>
    public bool RequiredImplied { get; init; }

    /// <summary>Effective client-facing required flag.</summary>
    public bool Required => RequiredExplicit || RequiredImplied;

    public static ValidationRules ForColumn(ColumnDto column)
    {
        var metadataMaxLength = Utils.MetadataNumber.PositiveIntOrNull(
            column.GetMetadataValue(MetadataKeys.Validation.MaxLength), MetadataKeys.Validation.MaxLength);
        return new ValidationRules
        {
            Min = Clean(column.GetMetadataValue(MetadataKeys.Validation.Min)),
            Max = Clean(column.GetMetadataValue(MetadataKeys.Validation.Max)),
            Step = Clean(column.GetMetadataValue(MetadataKeys.Validation.Step)),
            MinLength = Utils.MetadataNumber.PositiveIntOrNull(
                column.GetMetadataValue(MetadataKeys.Validation.MinLength), MetadataKeys.Validation.MinLength),
            MaxLength = metadataMaxLength ?? ExtractDbMaxLength(column.DataType),
            Pattern = Clean(column.GetMetadataValue(MetadataKeys.Validation.Pattern)),
            PatternMessage = Clean(column.GetMetadataValue(MetadataKeys.Validation.PatternMessage)),
            InputType = Clean(column.GetMetadataValue(MetadataKeys.Validation.InputType)),
            // Recognize the shared on/off switch vocabulary (true/on/yes/1/enabled…)
            // rather than only the literal "true"/"enabled", so a plausibly-truthy
            // `required` value is not silently treated as not-required.
            RequiredExplicit = Utils.MetadataSwitch.Parse(
                column.GetMetadataValue(MetadataKeys.Validation.Required), defaultValue: false),
            RequiredImplied = !column.IsNullable
                && !column.IsIdentity
                && !column.IsComputed
                && column.GetMetadataValue(MetadataKeys.AutoPopulate.Marker) == null,
        };
    }

    /// <summary>Min as a decimal bound, when it parses as one.</summary>
    public bool TryMinDecimal(out decimal value) => TryDecimalStr(Min, out value);

    /// <summary>Max as a decimal bound, when it parses as one.</summary>
    public bool TryMaxDecimal(out decimal value) => TryDecimalStr(Max, out value);

    /// <summary>Step as a decimal increment, when it parses as one.</summary>
    public bool TryStepDecimal(out decimal value) => TryDecimalStr(Step, out value);

    /// <summary>Min as a date/time bound, when it parses as one (ISO 8601 recommended).</summary>
    public bool TryMinDate(out DateTime value) => TryDateStr(Min, out value);

    /// <summary>Max as a date/time bound, when it parses as one (ISO 8601 recommended).</summary>
    public bool TryMaxDate(out DateTime value) => TryDateStr(Max, out value);

    /// <summary>
    /// Extracts the declared length from VARCHAR(255)-style type names;
    /// null for unbounded types like NVARCHAR(max).
    /// </summary>
    public static int? ExtractDbMaxLength(string? dataType)
    {
        if (string.IsNullOrEmpty(dataType))
            return null;

        // Only character types carry a usable length: DECIMAL(10,2) etc. must not
        // surface "10" as a string length.
        var normalized = dataType.TrimStart().ToUpperInvariant();
        if (!normalized.StartsWith("VARCHAR") && !normalized.StartsWith("NVARCHAR") &&
            !normalized.StartsWith("CHAR") && !normalized.StartsWith("NCHAR") &&
            !normalized.StartsWith("CHARACTER"))
            return null;

        var openParen = dataType.IndexOf('(');
        if (openParen < 0)
            return null;
        var closeParen = dataType.IndexOf(')', openParen);
        if (closeParen < 0)
            return null;

        var lengthStr = dataType.Substring(openParen + 1, closeParen - openParen - 1).Trim();
        return int.TryParse(lengthStr, out var length) && length > 0 ? length : null;
    }

    private static string? Clean(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static bool TryDecimalStr(string? raw, out decimal value)
    {
        value = 0;
        return raw != null && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryDateStr(string? raw, out DateTime value)
    {
        value = default;
        return raw != null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }
}

using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model;

/// <summary>
/// Parses schema facts out of a declared type string ("NVARCHAR(50)",
/// "DECIMAL(10,2)", "varbinary(16)"). Used where INFORMATION_SCHEMA facts are
/// unavailable — SQLite's PRAGMA reports only the declared text, and models
/// built without a schema reader (tests, EAV synthesis) carry bare type
/// strings. Engines with INFORMATION_SCHEMA capture these facts directly in
/// <see cref="ColumnDto.FromReader"/>; this is the fallback derivation.
/// </summary>
public static class DeclaredTypeFacts
{
    private static readonly string[] CharacterTypePrefixes =
        { "varchar", "nvarchar", "char", "nchar", "character", "text", "clob" };

    private static readonly string[] BinaryTypePrefixes =
        { "varbinary", "binary", "blob" };

    /// <summary>
    /// The declared length of a character or binary type, or null when the type
    /// carries none (unbounded, MAX, or not a length-bearing type). DECIMAL(10,2)
    /// must not surface "10" as a length.
    /// </summary>
    public static int? CharacterMaxLength(string? dataType)
        => LengthOf(dataType, CharacterTypePrefixes) ?? LengthOf(dataType, BinaryTypePrefixes);

    /// <summary>Declared precision/scale of an exact numeric type, else (null, null).</summary>
    public static (int? Precision, int? Scale) PrecisionScale(string? dataType)
    {
        if (dataType is null || !ColumnDto.IsExactNumeric(dataType))
            return (null, null);
        var args = ParenArguments(dataType);
        if (args.Length == 0 || !int.TryParse(args[0], out var precision) || precision <= 0)
            return (null, null);
        // A bare DECIMAL(10) declares scale 0, matching every engine's default.
        var scale = 0;
        if (args.Length > 1 && (!int.TryParse(args[1], out scale) || scale < 0))
            return (precision, null);
        return (precision, scale);
    }

    private static int? LengthOf(string? dataType, string[] prefixes)
    {
        var normalized = StringNormalizer.NormalizeType(dataType);
        if (!prefixes.Any(normalized.StartsWith))
            return null;
        var args = ParenArguments(normalized);
        return args.Length == 1 && int.TryParse(args[0], out var length) && length > 0
            ? length
            : null;
    }

    private static string[] ParenArguments(string dataType)
    {
        var open = dataType.IndexOf('(');
        if (open < 0)
            return Array.Empty<string>();
        var close = dataType.IndexOf(')', open);
        if (close < 0)
            return Array.Empty<string>();
        return dataType.Substring(open + 1, close - open - 1)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}

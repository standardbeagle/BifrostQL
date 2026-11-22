using System.Globalization;
using System.Numerics;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model;

/// <summary>
/// Turns a primary-key value into the one form that names its row, whichever surface wrote it.
/// A typed key from the host API and the positional <c>_primaryKey</c> strings a client sends
/// (<c>"05"</c>, <c>"5.0"</c>, an upper-case GUID, any spelling of an instant) reach the same
/// row in the database, so every record that identifies the row by its key (the history
/// trail's <c>entity_id</c>, the outbox payload and its CloudEvents subject) must spell it the
/// same way too.
///
/// <para>The canonical value per column family:</para>
/// <list type="bullet">
///   <item>integer: <see cref="long"/> (a <see cref="decimal"/> past its range, as an unsigned
///     64-bit column can hold);</item>
///   <item>decimal: <see cref="decimal"/> without trailing zeros; float: <see cref="double"/>;</item>
///   <item>boolean: <see cref="bool"/>; uuid: <see cref="Guid"/>;</item>
///   <item>date-time: the wall clock in round-trip form, <c>2026-03-01T12:00:00.1000000</c>
///     (an offset in client text is dropped, as the engines do for a zone-less column);</item>
///   <item>date-time with offset: the UTC instant in round-trip form,
///     <c>2026-03-01T10:00:00.0000000+00:00</c> (engines compare these by instant);</item>
///   <item>date: <c>yyyy-MM-dd</c>, the calendar day with no time part;</item>
///   <item>time (zone-less): <c>HH:mm:ss.fffffff</c>.</item>
/// </list>
///
/// <para>Every other column type (text, binary, a time with zone, an engine-specific type)
/// passes through unchanged, and so does a value whose CLR type the family does not
/// read (a host-supplied <c>byte[]</c> for a uuid): this only ever normalises, it never
/// refuses a write over a type it does not model, and no conversion loses precision. The one
/// refusal is client text that does not read as the column's type at all; that is a client
/// error (<see cref="BifrostExecutionError.InvalidValueCode"/>), never a 500.</para>
///
/// <para>Only the recorded copy of a key is canonicalised. The write binds the client's
/// value as given: converting a uuid string to <see cref="Guid"/> changes how
/// Microsoft.Data.Sqlite binds it against a TEXT column.</para>
/// </summary>
internal static class KeyValueCanonicalizer
{
    public static object? Canonicalize(ITypeMapper typeMapper, ColumnDto column, object? value)
    {
        if (value is null)
            return null;

        var dataType = column.EffectiveDataType;
        if (typeMapper.IsRowVersion(dataType))
            return value;

        var temporal = ValidationRules.TemporalKindOf(dataType);
        if (temporal != TemporalKind.None)
            return CanonicalTemporal(temporal, dataType, column, value);

        var clrType = ContextValueCoercer.GetClrType(typeMapper, column);
        if (clrType == typeof(Guid))
            return value switch
            {
                Guid guid => guid,
                string text => Guid.TryParse(text.Trim(), out var parsed) ? parsed : throw Unreadable(column, "uuid"),
                _ => value,
            };
        if (clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short) || clrType == typeof(byte))
            return CanonicalInteger(column, value);
        if (clrType == typeof(decimal))
            return CanonicalDecimal(column, value);
        if (clrType == typeof(double))
            return CanonicalFloat(column, value);
        if (clrType == typeof(bool))
            return CanonicalBoolean(column, value);
        return value;
    }

    private static object CanonicalInteger(ColumnDto column, object value)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case ulong u:
                return u <= long.MaxValue ? (long)u : (decimal)u;
            case BigInteger big:
                return big >= long.MinValue && big <= long.MaxValue ? (long)big : (object)(decimal)big;
            case decimal d when decimal.Truncate(d) == d:
                return IntegralDecimal(d);
            case string text:
                // Engines read "05", " 5 " and (SQLite) "5.0" as row 5; so does this.
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                    return whole;
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    && decimal.Truncate(number) == number)
                    return IntegralDecimal(number);
                throw Unreadable(column, "integer");
            default:
                return value;
        }
    }

    private static object IntegralDecimal(decimal d)
        => d >= long.MinValue && d <= long.MaxValue ? (long)d : StripTrailingZeros(d);

    private static object CanonicalDecimal(ColumnDto column, object value) => value switch
    {
        decimal d => StripTrailingZeros(d),
        sbyte or byte or short or ushort or int or uint or long or ulong => StripTrailingZeros(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        string text => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? StripTrailingZeros(parsed)
            : throw Unreadable(column, "decimal number"),
        _ => value,
    };

    // 5.00m and 5m are one key; dividing by 1 at maximum scale drops the trailing zeros.
    private static decimal StripTrailingZeros(decimal d) => d / 1.0000000000000000000000000000m;

    private static object CanonicalFloat(ColumnDto column, object value) => value switch
    {
        double d => d,
        float f => (double)f,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Unreadable(column, "number"),
        _ => value,
    };

    private static object CanonicalBoolean(ColumnDto column, object value) => value switch
    {
        bool b => b,
        sbyte or byte or short or ushort or int or uint or long or ulong when Convert.ToInt64(value, CultureInfo.InvariantCulture) is 0 or 1
            => Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1,
        // The union of the engines' boolean literals (SQL Server bit, PostgreSQL boolean).
        string text => StringNormalizer.Normalize(text) switch
        {
            "true" or "t" or "yes" or "y" or "on" or "1" => true,
            "false" or "f" or "no" or "n" or "off" or "0" => false,
            _ => throw Unreadable(column, "boolean"),
        },
        _ => value,
    };

    private static object CanonicalTemporal(TemporalKind kind, string dataType, ColumnDto column, object value)
    {
        switch (kind)
        {
            case TemporalKind.DateTimeOffset:
                DateTimeOffset? instant = value switch
                {
                    DateTimeOffset dto => dto,
                    // A zone-less instant is UTC, as SQL Server reads one for datetimeoffset.
                    DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero),
                    string text => ParseInstant(column, text, "date-time"),
                    _ => null,
                };
                return instant is { } exact
                    ? exact.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : value;

            case TemporalKind.DateTime:
                DateTime? clock = value switch
                {
                    DateTime dt => dt,
                    DateTimeOffset dto => dto.DateTime,
                    string text => ParseInstant(column, text, "date-time").DateTime,
                    _ => null,
                };
                return clock is { } wall
                    ? DateTime.SpecifyKind(wall, DateTimeKind.Unspecified).ToString("o", CultureInfo.InvariantCulture)
                    : value;

            case TemporalKind.DateOnly:
                DateOnly? day = value switch
                {
                    DateOnly d => d,
                    DateTime dt => DateOnly.FromDateTime(dt),
                    DateTimeOffset dto => DateOnly.FromDateTime(dto.DateTime),
                    string text => DateOnly.FromDateTime(ParseInstant(column, text, "date").DateTime),
                    _ => null,
                };
                return day is { } calendarDay
                    ? calendarDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : value;

            case TemporalKind.TimeOnly:
                // A time with zone has no canonical instant without a date; it passes through.
                var normalized = StringNormalizer.NormalizeType(dataType);
                if (normalized.Contains("tz") || normalized.Contains("with time zone"))
                    return value;
                TimeOnly? time = value switch
                {
                    TimeOnly t => t,
                    TimeSpan ts when ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(ts),
                    string text => ParseTime(column, text),
                    _ => null,
                };
                return time is { } clockTime
                    ? clockTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
                    : value;

            default:
                return value;
        }
    }

    // The offset as written is kept (AssumeUniversal only fills a missing one), so the wall
    // clock (.DateTime) is the text's own and the instant is exact.
    private static DateTimeOffset ParseInstant(ColumnDto column, string text, string noun)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
               DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw Unreadable(column, noun);

    private static TimeOnly ParseTime(ColumnDto column, string text)
    {
        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
            return time;
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1))
            return TimeOnly.FromTimeSpan(span);
        throw Unreadable(column, "time");
    }

    private static BifrostExecutionError Unreadable(ColumnDto column, string noun)
        => new($"Primary-key column '{column.ColumnName}' value is not a valid {noun}.")
        {
            ErrorCode = BifrostExecutionError.InvalidValueCode,
        };
}

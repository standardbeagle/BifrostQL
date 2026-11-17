using System.Globalization;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Deferred;

/// <summary>
/// Reads a change-set timestamp back as UTC. The hooks write UTC values; providers return them as
/// a <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/> (SQL Server
/// <c>datetime2</c>, Postgres <c>timestamp</c>), a UTC <see cref="DateTime"/>, a
/// <see cref="DateTimeOffset"/>, or text (SQLite). The host's time zone and culture never enter:
/// parsing <c>value.ToString()</c> with the current culture read an unmarked value as local time,
/// so the undo window closed early east of UTC and stayed open late west of it.
/// </summary>
internal static class DeferredTimestamp
{
    public static DateTimeOffset ReadUtc(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local.ToUniversalTime()),
        DateTime unmarked => new DateTimeOffset(DateTime.SpecifyKind(unmarked, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new BifrostExecutionError("A deferred change set timestamp has an unsupported type."),
    };
}

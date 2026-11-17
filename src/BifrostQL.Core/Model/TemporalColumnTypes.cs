using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model;

/// <summary>
/// The one list of column types that hold a point in time, across the supported dialects. Feed
/// timestamps, retention anchors, chat <c>created-at</c> columns and LDAP GeneralizedTime
/// attributes all ask <see cref="IsTimestamp"/>; none keeps its own list.
///
/// <para>Postgres reports <c>information_schema.data_type</c>, so its spellings are the long
/// forms (<c>timestamp with time zone</c>). Time-of-day types (<c>time</c>) are not instants and
/// are excluded.</para>
///
/// <para>Bare <c>timestamp</c> is ambiguous: a date-time on MySQL and SQLite, but a synonym for
/// <c>rowversion</c> (an opaque binary row counter) on SQL Server. The model-free callers here
/// cannot tell the dialect apart, so <see cref="IsTimestamp"/> accepts it, and LDAP, the one
/// consumer that publishes a wire syntax from the type, classifies it as binary
/// (<see cref="IsUnambiguousTimestamp"/>).</para>
/// </summary>
internal static class TemporalColumnTypes
{
    /// <summary>The bare type name that means rowversion on SQL Server and date-time elsewhere.</summary>
    public const string AmbiguousTimestamp = "timestamp";

    /// <summary>Every accepted point-in-time type, lower-case.</summary>
    public static readonly IReadOnlySet<string> Timestamp =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "datetime", "datetime2", "smalldatetime", "datetimeoffset", "date",
            AmbiguousTimestamp, "timestamptz",
            "timestamp with time zone", "timestamp without time zone",
        };

    /// <summary>True when <paramref name="dataType"/> holds a point in time.</summary>
    public static bool IsTimestamp(string? dataType)
        => Timestamp.Contains(StringNormalizer.NormalizeType(dataType));

    /// <summary>
    /// True when <paramref name="dataType"/> holds a point in time on every dialect: every
    /// <see cref="Timestamp"/> member except bare <see cref="AmbiguousTimestamp"/>.
    /// </summary>
    public static bool IsUnambiguousTimestamp(string? dataType)
    {
        var type = StringNormalizer.NormalizeType(dataType);
        return type != AmbiguousTimestamp && Timestamp.Contains(type);
    }
}

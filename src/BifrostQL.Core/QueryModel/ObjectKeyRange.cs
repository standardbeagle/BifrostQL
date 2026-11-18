namespace BifrostQL.Core.QueryModel;

/// <summary>
/// How one primary-key column renders as text inside an S3 object key. Each kind names a
/// family whose database text cast is byte-identical to
/// <see cref="Storage.S3ObjectKeyMap"/>'s C# rendering of the value the reader returns, so
/// <see cref="ISqlDialect.ObjectKeyExpression"/> can compute the key in SQL. A key column
/// outside these families has no such expression (<see cref="Storage.S3ObjectKeyMap.SegmentKindOf"/>
/// returns null).
/// </summary>
public enum ObjectKeySegmentKind
{
    /// <summary>Integer types: invariant decimal text, which escaping leaves unchanged.</summary>
    Integer,

    /// <summary>UUID types: lowercase 8-4-4-4-12 hex, which escaping leaves unchanged.</summary>
    Uuid,

    /// <summary>Variable-length text: the UTF-8 bytes percent-encoded outside <c>[A-Za-z0-9_-]</c>.</summary>
    Text,
}

/// <summary>
/// One primary-key column handed to <see cref="ISqlDialect.ObjectKeyExpression"/>: its
/// already-escaped, table-qualified column reference and how it renders as key text.
/// </summary>
public readonly record struct ObjectKeySegmentColumn(string ColumnExpression, ObjectKeySegmentKind Kind);

/// <summary>
/// A keyset window over a row's object-key text: the primary-key segments joined by
/// <c>/</c>, exactly as <see cref="Storage.S3ObjectKeyMap.KeyFor"/> renders them after the
/// column segment. A node carrying one is ordered by that text in binary order and filtered
/// to <c>key &gt; After AND key &gt;= From AND key &lt; Before</c> (each bound optional).
/// The bounds are bound as parameters, never spliced into SQL text.
/// </summary>
/// <param name="After">Exclusive lower bound (a resume position), or null.</param>
/// <param name="From">Inclusive lower bound (a prefix start), or null.</param>
/// <param name="Before">Exclusive upper bound (a prefix's successor), or null.</param>
public sealed record ObjectKeyRange(string? After, string? From, string? Before);

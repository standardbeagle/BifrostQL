using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Utils;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL dialect implementation.
/// Uses double-quote identifiers ("name"), LIMIT/OFFSET pagination,
/// '||' for string concatenation, and lastval() for last inserted identity.
/// </summary>
public sealed class PostgresDialect : StandardConcatDialectBase
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PostgresDialect Instance = new();

    // `RETURNING id AS ID` assumes every table's identity column is literally
    // called `id`, which is wrong for the common `<table>_id` Postgres
    // convention. Drop the appended RETURNING clause and let the resolver
    // fall back to `SELECT lastval() ID`, which returns the sequence value
    // produced by the most recent INSERT in this session.
    public PostgresDialect() : base('"', "lastval()", null)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// information_schema reports custom types (Apache AGE's graphid/agtype, and any
    /// other user-defined type) as data_type 'USER-DEFINED'. Npgsql cannot read these
    /// as object, so they are cast to text in the SELECT and surfaced as GraphQL String.
    /// Temporal and other non-character PostgreSQL values that resolve to GraphQL String
    /// are also cast so GraphQL receives an actual CLR string, not a provider-specific
    /// CLR value such as DateTime, Guid, IPAddress, or TimeSpan.
    /// </remarks>
    public override bool RequiresTextCast(string dataType) =>
        RequiresTextCast(dataType, PostgresTypeMapper.Instance.GetGraphQlType(dataType));

    /// <inheritdoc />
    public override bool RequiresTextCast(string dataType, string graphQlType)
    {
        var t = StringNormalizer.NormalizeType(dataType);
        if (t is "json" or "jsonb")
            return false;

        if (IsTemporalType(t) || t is "user-defined")
            return true;

        return string.Equals(graphQlType, "String", StringComparison.Ordinal)
            && !IsNativeStringType(t);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses format('%s', expr) rather than expr::text. The ::text cast only works on
    /// scalar agtype — a non-scalar agtype map/list (Apache AGE node/edge properties)
    /// raises "agtype argument must resolve to a scalar value". format('%s', ...) routes
    /// through the type's output function, which serializes graphid, scalar agtype, and
    /// agtype maps/lists alike (and leaves ordinary types unchanged).
    /// </remarks>
    public override string TextCast(string columnExpression) => $"format('%s', {columnExpression})";

    /// <inheritdoc />
    public override string TextCast(string columnExpression, string dataType)
    {
        var t = StringNormalizer.NormalizeType(dataType);
        return IsTemporalType(t)
            ? $"to_jsonb({columnExpression}) #>> '{{}}'"
            : TextCast(columnExpression);
    }

    internal static bool IsTemporalType(string dataType)
    {
        var t = StringNormalizer.NormalizeType(dataType);
        return t is "date" or "time" or "timetz"
            or "time with time zone" or "time without time zone"
            or "timestamp" or "timestamptz"
            or "timestamp with time zone" or "timestamp without time zone"
            or "interval";
    }

    private static bool IsNativeStringType(string dataType)
    {
        var t = StringNormalizer.NormalizeType(dataType);
        return t is "character varying" or "varchar"
            or "character" or "char"
            or "text";
    }
}

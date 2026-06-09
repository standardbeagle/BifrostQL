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
    /// For a single-column primary key, RETURNING the real key column makes the insert
    /// work for ANY key type — serial/bigserial, uuid (server-default gen_random_uuid()),
    /// or a client-supplied value — because it reads the row's own key rather than the
    /// session's <c>lastval()</c>, which is only defined when a sequence was advanced and
    /// throws "lastval is not yet defined in this session" for uuid/non-sequence keys.
    ///
    /// Composite (or absent) primary keys fall back to <c>lastval()</c> (return null):
    /// a multi-column key can't be projected into the single scalar identity the caller
    /// reads via ExecuteScalar, and changing that contract is out of scope here.
    /// </remarks>
    public override string? ReturningIdentityClauseFor(IReadOnlyList<string> keyColumns)
    {
        if (keyColumns.Count != 1)
            return null;
        return $" RETURNING {EscapeIdentifier(keyColumns[0])} AS ID";
    }

    /// <inheritdoc />
    /// <remarks>
    /// Npgsql binds a CLR string parameter as an explicit <c>text</c> type. Postgres
    /// applies a cast to an <em>unknown</em>-typed literal but NOT to a text-typed bind
    /// parameter, so <c>SET started_at = $1</c> (assignment) and <c>week_of = $1</c>
    /// (comparison) both fail with a string value ("expression is of type text" /
    /// "operator does not exist: date = text") even though the equivalent literal succeeds.
    /// Casting the reference to the column's type (<c>$1::date</c>) restores the literal-like
    /// behavior. Drives both <see cref="ISqlDialect.AssignmentPlaceholder"/> (writes) and
    /// WHERE-clause filter parameters (reads).
    ///
    /// Uses an allow-list of types that (a) genuinely need the cast — Postgres won't
    /// implicitly compare/assign them against a text parameter — and (b) name a real
    /// Postgres type. Anything else stays bare: string types (text/varchar) compare to a
    /// text parameter fine, and an unrecognised type name (e.g. a model carrying the
    /// SqlServer-style <c>nvarchar</c> for a column that is really <c>text</c> in PG) must
    /// NOT be emitted as <c>::nvarchar</c> — that raises 42704 "type does not exist".
    /// The cast target is the normalized type, which for these entries is always valid PG
    /// syntax (e.g. <c>timestamp with time zone</c>, <c>uuid</c>, <c>jsonb</c>).
    /// </remarks>
    public override string CastParameterReference(string placeholder, string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            return placeholder;

        var t = StringNormalizer.NormalizeType(dataType);
        return NeedsParameterCast(t) ? $"{placeholder}::{t}" : placeholder;
    }

    /// <summary>
    /// Whether a text-bound parameter must be cast to compare against / assign to a column
    /// of this (normalized) Postgres type. Restricted to real PG type names so the cast is
    /// always valid SQL; unknown/string/user-defined/array types return false (stay bare).
    /// </summary>
    internal static bool NeedsParameterCast(string normalizedType) =>
        IsTemporalType(normalizedType)
        || normalizedType is "uuid"
            or "json" or "jsonb"
            or "boolean" or "bool"
            or "smallint" or "integer" or "int" or "int2" or "int4" or "int8" or "bigint"
            or "numeric" or "decimal" or "real" or "double precision" or "float4" or "float8"
            or "money" or "bytea" or "inet" or "cidr" or "macaddr";

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

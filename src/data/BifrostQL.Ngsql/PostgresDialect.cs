using BifrostQL.Core.Model;
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
    /// Postgres locks a selected row with the standard trailing <c>FOR UPDATE</c> clause,
    /// held until the transaction ends. The change-history before-image read uses it so a
    /// concurrent writer blocks instead of committing between the pre-image read and the
    /// UPDATE it precedes.
    /// </remarks>
    public override string UpdateLockClause => " FOR UPDATE";

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

    /// <summary>Singular interval unit for <c>INTERVAL '1 &lt;unit&gt;'</c> arithmetic.</summary>
    private static string IntervalUnit(DateUnit unit) => unit switch
    {
        DateUnit.Year => "year",
        DateUnit.Month => "month",
        DateUnit.Day => "day",
        DateUnit.Hour => "hour",
        DateUnit.Minute => "minute",
        DateUnit.Second => "second",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <summary>The <c>EXTRACT(&lt;field&gt; FROM …)</c> field name.</summary>
    private static string ExtractField(DateUnit unit) => unit switch
    {
        DateUnit.Year => "YEAR",
        DateUnit.Month => "MONTH",
        DateUnit.Day => "DAY",
        DateUnit.Hour => "HOUR",
        DateUnit.Minute => "MINUTE",
        DateUnit.Second => "SECOND",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <inheritdoc />
    /// <remarks>
    /// Interval arithmetic: <c>(&lt;source&gt; + (&lt;amount&gt; * INTERVAL '1 day'))</c>. Multiplying
    /// a unit interval by the bound amount handles any signed count; the interval unit is a fixed
    /// keyword, never client text.
    /// </remarks>
    protected override string LowerDateAdd(SqlExpr.DateAdd node, IDbTable table, SqlParameterCollection parameters)
    {
        var amount = LowerExpression(node.Amount, table, parameters);
        var source = LowerExpression(node.Source, table, parameters);
        return $"({source} + ({amount} * INTERVAL '1 {IntervalUnit(node.Unit)}'))";
    }

    /// <inheritdoc />
    /// <remarks>
    /// PostgreSQL has no single <c>DATEDIFF</c> primitive. For the epoch-computable units
    /// (day/hour/minute/second) the difference is <c>FLOOR(EXTRACT(EPOCH FROM (end - start)) /
    /// &lt;seconds&gt;)</c>. Whole months/years are NOT epoch-computable — a fixed seconds divisor
    /// cannot count calendar boundaries — so they fail fast with
    /// <see cref="SqlExprLoweringNotSupportedException"/> rather than emit a silently-wrong
    /// approximation.
    /// </remarks>
    protected override string LowerDateDiff(SqlExpr.DateDiff node, IDbTable table, SqlParameterCollection parameters)
    {
        var secondsPerUnit = node.Unit switch
        {
            DateUnit.Day => 86400,
            DateUnit.Hour => 3600,
            DateUnit.Minute => 60,
            DateUnit.Second => 1,
            _ => throw new SqlExprLoweringNotSupportedException(
                nameof(SqlExpr.DateDiff), "PostgreSQL",
                $"whole-{node.Unit.ToString().ToLowerInvariant()} difference cannot be computed exactly " +
                "from an epoch delta (calendar boundaries vary); use DatePart-based arithmetic instead.")
        };

        var start = LowerExpression(node.Start, table, parameters);
        var end = LowerExpression(node.End, table, parameters);
        return $"FLOOR(EXTRACT(EPOCH FROM ({end} - {start})) / {secondsPerUnit})";
    }

    /// <inheritdoc />
    /// <remarks><c>EXTRACT(YEAR FROM &lt;source&gt;)</c>.</remarks>
    protected override string LowerDatePart(SqlExpr.DatePart node, IDbTable table, SqlParameterCollection parameters)
    {
        var source = LowerExpression(node.Source, table, parameters);
        return $"EXTRACT({ExtractField(node.Unit)} FROM {source})";
    }

    /// <inheritdoc />
    /// <remarks>
    /// jsonb path traversal with the <c>-&gt;</c> / <c>-&gt;&gt;</c> operators: each intermediate
    /// segment uses <c>-&gt;</c> (returns jsonb) and the final segment uses <c>-&gt;&gt;</c>
    /// (returns the scalar as text). Segments are <see cref="JsonPath"/>-validated, so the
    /// single-quoted key literals cannot break out.
    /// </remarks>
    protected override string LowerJsonGet(SqlExpr.JsonGet node, IDbTable table, SqlParameterCollection parameters)
    {
        var expr = LowerExpression(node.Source, table, parameters);
        var segments = node.Path.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            var op = i == segments.Count - 1 ? "->>" : "->";
            expr = $"({expr} {op} '{segments[i]}')";
        }
        return expr;
    }

    /// <inheritdoc />
    /// <remarks>
    /// PostgreSQL full-text search matches <c>to_tsvector(cfg, doc) @@ tsquery</c> against a
    /// GIN index on the tsvector of the concatenated searchable columns (the prerequisite
    /// the FTS guide documents). The term VALUES are fed through <c>plainto_tsquery</c> /
    /// <c>phraseto_tsquery</c>, which take PLAIN TEXT and normalize it into a tsquery
    /// safely — they are NOT injectable (unlike raw <c>to_tsquery</c>), so a bound term can
    /// carry any characters without breaking out. Terms are ANDed at the SQL level to honor
    /// the pinned multi-term AND semantic (rather than <c>websearch_to_tsquery</c>, whose
    /// operator handling differs); a phrase term uses <c>phraseto_tsquery</c> so its words
    /// must be adjacent. The optional language is bound and cast to <c>regconfig</c>; when
    /// absent the server's default text-search config applies. to_tsvector lower-cases via
    /// the config, so matching is case-insensitive.
    /// </remarks>
    public override ParameterizedSql SearchPredicate(FtsPredicateRequest request)
    {
        RequireSearchable(request);
        var start = request.Parameters.Parameters.Count();

        // Alias-qualified so the tsvector is built from the searched table's columns and
        // not from a same-named column of another relation in scope.
        var doc = string.Join(" || ' ' || ",
            request.ColumnNames.Select(c => $"coalesce({SearchColumnRef(request, c)}, '')"));

        string? langRef = null;
        if (!string.IsNullOrWhiteSpace(request.Language))
            langRef = $"{request.Parameters.AddParameter(request.Language)}::regconfig";

        var tsvector = langRef == null ? $"to_tsvector({doc})" : $"to_tsvector({langRef}, {doc})";

        var predicates = request.Terms.Select(term =>
        {
            var p = request.Parameters.AddParameter(term.Text);
            var fn = term.IsPhrase ? "phraseto_tsquery" : "plainto_tsquery";
            var tsquery = langRef == null ? $"{fn}({p})" : $"{fn}({langRef}, {p})";
            return $"({tsvector}) @@ {tsquery}";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}

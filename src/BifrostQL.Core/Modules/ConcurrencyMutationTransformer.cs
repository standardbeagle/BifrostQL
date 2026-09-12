using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Optimistic-concurrency (lost-update prevention) on the write path. For a table
/// carrying <c>concurrency-token: &lt;column&gt;</c>, an UPDATE must carry the token
/// value the client last read. This transformer:
/// <list type="bullet">
/// <item>ANDs <c>token = @clientVersion</c> into the UPDATE WHERE (via AdditionalFilter),
/// so a row whose token has moved matches nothing;</item>
/// <item>bumps the token in the SET (increment for a numeric token, a fresh timestamp
/// for a datetime token) so every write advances it;</item>
/// <item>sets <see cref="MutationTransformResult.ConflictOnNoRows"/> so the executor
/// turns a zero-row update into a CONFLICT instead of a silent no-op.</item>
/// </list>
/// Configuration: <c>"dbo.orders { concurrency-token: row_version }"</c>.
///
/// Priority 60 — below soft-delete (100) so it runs before the DELETE→UPDATE rewrite,
/// and its predicate AND-combines with tenant/soft-delete/policy filters. Applies only
/// to UPDATE (an INSERT starts a new row; a DELETE is not token-guarded here).
///
/// Token type support: numeric (incremented) and datetime (restamped). DB-managed
/// tokens (SQL Server <c>rowversion</c>, Postgres <c>xmin</c>) are rejected with a
/// clear error rather than silently left un-bumped — supporting them is a documented
/// follow-up.
/// </summary>
public sealed class ConcurrencyMutationTransformer : MetadataMutationTransformerBase
{
    public const string MetadataKey = MetadataKeys.Concurrency.Token;

    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "Int", "Short", "Byte", "BigInt", "Decimal",
    };
    private static readonly HashSet<string> DateTimeTypes = new(StringComparer.Ordinal)
    {
        "DateTime", "DateTimeOffset",
    };

    public ConcurrencyMutationTransformer() : base(MetadataKey, priority: 60) { }

    public override string ModuleName => MetadataKeys.Concurrency.Token;

    /// <summary>Guards writes only — an INSERT has no prior version, a DELETE is not token-scoped here.</summary>
    public override bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
        => mutationType == MutationType.Update && base.AppliesTo(table, mutationType, context);

    protected override MutationTransformResult TransformCore(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName)
    {
        var column = table.ColumnLookup[columnName];

        // The chain rekeys its input to database column names before the first
        // transformer runs, so the token is addressed by its column name here.
        var tokenKey = data.Keys.FirstOrDefault(k =>
            string.Equals(k, columnName, StringComparison.OrdinalIgnoreCase));

        if (tokenKey == null || data[tokenKey] == null)
            return Rejected(
                $"The update must include the concurrency token column '{column.GraphQlName}' (the version the row was read at).",
                $"Missing concurrency token '{columnName}' on table '{table.TableSchema}.{table.DbName}'.",
                mutationType, data);

        var clientVersion = data[tokenKey]!;
        if (!TryBump(context.Model.TypeMapper.GetGraphQlType(column.EffectiveDataType), clientVersion, out var bumped, out var reason))
            return Rejected(
                $"The concurrency token '{column.GraphQlName}' {reason}.",
                $"Unbumpable concurrency token '{columnName}' on table '{table.TableSchema}.{table.DbName}': {reason}.",
                mutationType, data);

        // SET the bumped token; WHERE guards on the client's value (bound as a param).
        var next = new Dictionary<string, object?>(data) { [tokenKey] = bumped };
        return new MutationTransformResult
        {
            MutationType = MutationType.Update,
            Data = next,
            AdditionalFilter = TableFilterFactory.Equals(table, columnName, clientVersion),
            ConflictOnNoRows = true,
        };
    }

    /// <summary>
    /// Computes the next token: a numeric token increments, a datetime token restamps
    /// to now. Any other type is unsupported (DB-managed tokens are rejected upstream).
    /// </summary>
    private static bool TryBump(string graphQlType, object clientVersion, out object? bumped, out string reason)
    {
        if (NumericTypes.Contains(graphQlType))
        {
            try
            {
                // A Decimal token keeps decimal range and precision; integer tokens use
                // checked long arithmetic so an at-max token surfaces as a clean error,
                // not a silent wrap to a negative value or an unhandled OverflowException.
                bumped = graphQlType == "Decimal"
                    ? checked(Convert.ToDecimal(clientVersion) + 1m)
                    : (object)checked(Convert.ToInt64(clientVersion) + 1);
            }
            catch (OverflowException)
            {
                bumped = null;
                reason = "carried a version value outside the representable range and cannot be advanced";
                return false;
            }
            reason = "";
            return true;
        }
        if (DateTimeTypes.Contains(graphQlType))
        {
            bumped = DateTimeOffset.UtcNow;
            reason = "";
            return true;
        }
        bumped = null;
        reason = $"has unsupported type '{graphQlType}' — only numeric and datetime tokens are supported (DB-managed rowversion/xmin tokens are not yet supported)";
        return false;
    }

    private static MutationTransformResult Error(string message, MutationType mutationType, Dictionary<string, object?> data)
        => new() { MutationType = mutationType, Data = data, Errors = new[] { message } };

    /// <summary>
    /// Aborts the update with the identifier-free <paramref name="wireMessage"/> while
    /// recording the model-derived <paramref name="detail"/> (<c>schema.table</c>)
    /// server-side through the shared <see cref="BifrostErrorSink"/> seam — the same
    /// treatment the lost-update <c>CONFLICT</c> receives. The abort surfaces as a plain
    /// <see cref="MutationTransformResult.Errors"/> string through
    /// <c>MutationTransformersWrap</c>, which stamps no <c>ErrorCode</c> for these two
    /// pre-write conditions (they stay a generic fault), so none is invented here; the
    /// sink's sanitized exception is unused and kept only for its log/sanitize pair.
    /// </summary>
    private static MutationTransformResult Rejected(string wireMessage, string detail, MutationType mutationType, Dictionary<string, object?> data)
    {
        _ = BifrostErrorSink.Sanitized(wireMessage, detail, nameof(TransformCore));
        return Error(wireMessage, mutationType, data);
    }
}

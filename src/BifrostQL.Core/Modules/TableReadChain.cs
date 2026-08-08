using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Modules;

/// <summary>
/// How rows read through a <see cref="TableReadChain"/> are projected. Every read
/// surface must state which it is; there is no default, because the two differ in
/// exactly the way that matters for encrypted columns.
/// </summary>
public enum ReadProjection
{
    /// <summary>
    /// The rows are returned to the caller. Encrypted columns are decrypted only for
    /// a caller holding the column's <c>unmask-role</c>, otherwise masked per its
    /// <c>mask</c> mode. Raw ciphertext never leaves.
    /// </summary>
    Client,

    /// <summary>
    /// The rows are consumed SERVER-SIDE ONLY and never reach the caller — currently
    /// the tree-sync state load, whose loaded row exists solely to be diffed against
    /// the submitted tree. Encrypted columns are decrypted regardless of caller role,
    /// because the comparison is between a submitted PLAINTEXT and a stored
    /// CIPHERTEXT: masking (or leaving the envelope) makes every comparison unequal,
    /// so an unchanged encrypted field is rewritten on every sync. A surface choosing
    /// this MUST NOT put the resulting values on any client-facing wire.
    /// </summary>
    InternalDiff,
}

/// <summary>
/// THE read chain a surface must apply before its rows reach a caller: the combined
/// row filter (tenant / soft-delete / row-scope policy), the column read guard over
/// the projected columns, the column filter guard over any column used in a
/// predicate / GROUP BY / sort / distinct-discovery position, and the crypto read
/// projection over the returned values.
///
/// It exists because there were five hand-rolled copies of that chain and three of
/// them had drifted — <c>_table</c> returned policy-denied columns and raw
/// ciphertext, <c>_meta</c> and tree-sync applied only the ROW half, and the pivot
/// attached its GROUP BY columns in a read-only position so they never met the
/// filter guard. This type is the single funnel: same shape as
/// <c>.claude/rules/protocol-adapter-security.md</c> invariant 10, read from the
/// read-authorization side rather than the error-mapping side.
///
/// It decides NOTHING itself. Readability is whatever the registered
/// <see cref="IColumnReadGuard"/>s say, filterability whatever the
/// <see cref="IColumnFilterGuard"/>s say, row scope whatever
/// <see cref="IFilterTransformers.GetCombinedFilter"/> returns — no second
/// authorization rule is implemented here (invariant 4).
///
/// A new read surface should obtain its rows through <see cref="ReadRowsAsync"/> (or,
/// where the result set is not a row map, <see cref="ProjectValue"/>), because that is
/// the only path on which the crypto projection is applied. Reading a
/// <see cref="DbDataReader"/> directly bypasses it.
/// </summary>
public sealed class TableReadChain
{
    private readonly IFilterTransformers? _filterTransformers;
    private readonly QueryTransformContext? _transformContext;
    private readonly CryptoReadProjector _crypto;
    private readonly ReadProjection _projection;
    private IReadOnlyList<ColumnDto>? _readableColumns;

    private TableReadChain(
        IDbTable table,
        IFilterTransformers? filterTransformers,
        QueryTransformContext? transformContext,
        CryptoReadProjector crypto,
        ReadProjection projection)
    {
        Table = table;
        _filterTransformers = filterTransformers;
        _transformContext = transformContext;
        _crypto = crypto;
        _projection = projection;
    }

    public IDbTable Table { get; }

    /// <summary>
    /// Builds the chain for one table from the request's services and user context.
    ///
    /// When no <see cref="IFilterTransformers"/> is registered the guard/row-filter
    /// halves degrade to "no restriction", matching how the rest of the read path
    /// behaves in lightweight hosts. The crypto projection is built REGARDLESS: with
    /// no <see cref="EnvelopeKeyManager"/> resolvable it redacts rather than
    /// returning the envelope, so a missing registration can never turn into a
    /// ciphertext leak.
    /// </summary>
    public static TableReadChain For(
        IServiceProvider? services,
        IDbModel model,
        IDbTable table,
        IDictionary<string, object?> userContext,
        ReadProjection projection,
        QueryType queryType = QueryType.Standard,
        string path = "",
        bool isNestedQuery = false)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (table is null) throw new ArgumentNullException(nameof(table));

        var context = userContext ?? new Dictionary<string, object?>();
        var filterTransformers = services?.GetService<IFilterTransformers>();

        var transformContext = new QueryTransformContext
        {
            Model = model,
            UserContext = context,
            QueryType = queryType,
            Path = string.IsNullOrEmpty(path) ? table.GraphQlName : path,
            IsNestedQuery = isNestedQuery,
        };

        // InternalDiff decrypts unconditionally; the admin role is how
        // CryptoReadProjector expresses "no masking", and the rows never leave the
        // process. Client projections use the caller's real roles.
        var roles = projection == ReadProjection.InternalDiff
            ? new[] { MetadataKeys.Policy.DefaultAdminRole }
            : PolicyIdentity.ExtractRoles(context);

        var crypto = new CryptoReadProjector(
            model, services?.GetService<EnvelopeKeyManager>(), roles);

        return new TableReadChain(table, filterTransformers, transformContext, crypto, projection);
    }

    /// <summary>
    /// The combined tenant / soft-delete / row-scope-policy filter for this table, or
    /// null when nothing applies.
    /// </summary>
    public TableFilter? RowFilter =>
        _filterTransformers is null || _transformContext is null
            ? null
            : _filterTransformers.GetCombinedFilter(Table, _transformContext);

    /// <summary>
    /// The subset of the table's columns the caller may read, decided entirely by the
    /// registered <see cref="IColumnReadGuard"/>s: the whole set is offered first, and
    /// only if that is rejected is each column offered alone to find which are denied.
    /// A guard's throw excludes the column — fail-closed, with the guard as the single
    /// authority. Use this where the surface has no client selection set to reject
    /// against; where it has one, use <see cref="AssertReadable"/> so a denied column
    /// aborts the query as the ordinary path does.
    /// </summary>
    public IReadOnlyList<ColumnDto> ReadableColumns => _readableColumns ??= ComputeReadableColumns();

    /// <summary>
    /// Throws when any of <paramref name="columnDbNames"/> may not be READ. Reject
    /// semantics, matching the ordinary query path.
    /// </summary>
    public void AssertReadable(IEnumerable<string> columnDbNames)
    {
        if (_filterTransformers is null || _transformContext is null)
            return;

        var names = Normalize(columnDbNames);
        if (names.Length == 0)
            return;

        foreach (var guard in _filterTransformers.OfType<IColumnReadGuard>())
            guard.AssertColumnsReadable(Table, names, _transformContext);
    }

    /// <summary>
    /// Throws when any of <paramref name="columnDbNames"/> may not be used in a
    /// PREDICATE position — WHERE, ORDER BY, GROUP BY, an aggregate, or a
    /// distinct-value discovery. Predicate columns must clear BOTH guards: a
    /// predicate on an unreadable column is a value oracle whether or not the column
    /// is projected, and an encrypted column's ciphertext must not be probeable.
    /// </summary>
    public void AssertPredicateColumns(IEnumerable<string> columnDbNames)
    {
        if (_filterTransformers is null || _transformContext is null)
            return;

        var names = Normalize(columnDbNames);
        if (names.Length == 0)
            return;

        foreach (var guard in _filterTransformers.OfType<IColumnReadGuard>())
            guard.AssertColumnsReadable(Table, names, _transformContext);
        foreach (var guard in _filterTransformers.OfType<IColumnFilterGuard>())
            guard.AssertColumnsFilterable(Table, names, _transformContext);
    }

    /// <summary>
    /// Projects one column value: decrypt/mask for an encrypted column per this
    /// chain's <see cref="ReadProjection"/>, pass-through otherwise.
    /// </summary>
    public object? ProjectValue(string columnDbName, object? raw) =>
        _crypto.Project(Table.DbName, columnDbName, raw);

    /// <summary>
    /// Projects every value of a materialized row in place and returns it.
    /// </summary>
    public Dictionary<string, object?> ProjectRow(Dictionary<string, object?> row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        foreach (var key in row.Keys.ToArray())
            row[key] = ProjectValue(key, row[key]);
        return row;
    }

    /// <summary>
    /// Executes <paramref name="command"/> and materializes its rows THROUGH the
    /// crypto projection. This is the only row-materialization a read surface should
    /// use: reading the <see cref="DbDataReader"/> itself skips the projection, which
    /// is how encrypted columns previously left as raw envelopes.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ReadRowsAsync(
        DbCommand command,
        IEqualityComparer<string>? comparer = null,
        CancellationToken cancellationToken = default)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ProjectRow(DbReaderExtensions.ReadRow(reader, comparer)));
        return rows;
    }

    private static string[] Normalize(IEnumerable<string> columnDbNames) =>
        (columnDbNames ?? Array.Empty<string>())
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private IReadOnlyList<ColumnDto> ComputeReadableColumns()
    {
        var all = Table.Columns.ToList();
        if (_filterTransformers is null || _transformContext is null)
            return all;

        var guards = _filterTransformers.OfType<IColumnReadGuard>().ToArray();
        if (guards.Length == 0)
            return all;

        // Fast path: ask about the whole table once. A table with no denied column —
        // the common case — costs one guard call instead of one per column.
        if (AllReadable(guards, all.Select(c => c.DbName).ToArray()))
            return all;

        var readable = new List<ColumnDto>();
        foreach (var column in all)
        {
            if (AllReadable(guards, new[] { column.DbName }))
                readable.Add(column);
        }
        return readable;
    }

    private bool AllReadable(IReadOnlyList<IColumnReadGuard> guards, string[] columns)
    {
        foreach (var guard in guards)
        {
            try
            {
                guard.AssertColumnsReadable(Table, columns, _transformContext!);
            }
            catch (BifrostExecutionError)
            {
                return false;
            }
        }
        return true;
    }
}

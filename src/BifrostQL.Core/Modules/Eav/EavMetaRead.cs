using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// The guarded meta-table read shared by the two EAV providers (<see cref="EavMetaProvider"/>
/// for <c>_meta</c>, <see cref="EavMetaTruncatedProvider"/> for <c>_metaTruncated</c>): the
/// EAV config lookup, the meta table's full read chain, the row-scope filter and the parent
/// keys of the page. Both fields clear the same guards, so neither can reveal what the other
/// would refuse.
/// </summary>
internal sealed class EavMetaRead
{
    private EavMetaRead(
        EavConfig config, IDbTable metaTable, TableReadChain readChain, ISqlDialect dialect,
        SqlParameterCollection securityParams, string securityWhere, object?[] rowKeys, List<object> distinctKeys)
    {
        Config = config;
        MetaTable = metaTable;
        ReadChain = readChain;
        Dialect = dialect;
        SecurityParams = securityParams;
        SecurityWhere = securityWhere;
        RowKeys = rowKeys;
        DistinctKeys = distinctKeys;
        MetaTableRef = dialect.TableReference(config.TableSchema, config.MetaTableDbName);
        FkCol = dialect.EscapeIdentifier(config.ForeignKeyColumn);
    }

    public EavConfig Config { get; }
    public IDbTable MetaTable { get; }
    public TableReadChain ReadChain { get; }
    public ISqlDialect Dialect { get; }
    public SqlParameterCollection SecurityParams { get; }
    public string SecurityWhere { get; }
    public string MetaTableRef { get; }
    public string FkCol { get; }

    /// <summary>The parent key of each page row, in row order; null for a row without one.</summary>
    public object?[] RowKeys { get; }

    /// <summary>The page's distinct non-null parent keys.</summary>
    public List<object> DistinctKeys { get; }

    /// <summary>
    /// The read for this page, or null when there is nothing to read: the table is not an
    /// EAV parent, has a composite key, has no connection factory, or no row carries a key.
    /// </summary>
    public static async ValueTask<EavMetaRead?> PrepareAsync(ComputedColumnBatchContext context)
    {
        // Metadata-driven: locate the EAV config whose parent table is the table
        // currently being projected. Match on schema too — a same-named parent in a
        // different schema (app.settings vs dbo.settings) must not bind here. The
        // parent shares the meta table's schema (see EavConfigCollector), which the
        // config carries as TableSchema. No name-prefix detection.
        var config = context.Model.EavConfigs.FirstOrDefault(e =>
            string.Equals(e.ParentTableDbName, context.Table.DbName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.TableSchema, context.Table.TableSchema, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            return null;

        // Composite-PK limitation: the meta table references the parent via a
        // single foreign-key column, so a parent must have exactly one PK column
        // for the _meta lookup to be unambiguous. Tables with composite keys are
        // not supported by this provider (documented limitation).
        var keyColumns = context.Table.KeyColumns.ToArray();
        if (keyColumns.Length != 1 || context.ConnFactory is null)
            return null;

        var rowKeys = new object?[context.Rows.Count];
        var distinctKeys = new List<object>();
        var seen = new HashSet<ParentKey>();
        for (var i = 0; i < rowKeys.Length; i++)
        {
            var pk = ReadKeyValue(context.Rows[i], keyColumns[0]);
            rowKeys[i] = pk;
            if (pk is not null && seen.Add(ParentKey.Of(pk)))
                distinctKeys.Add(pk);
        }
        if (distinctKeys.Count == 0)
            return null;

        // Resolve the meta table by BOTH schema and DbName via the schema-qualified lookup:
        // a bare-name lookup can return a same-named meta table in a different schema and
        // apply the wrong table's security filter. Without the meta table in the model there
        // is no policy to consult and no column metadata to drive the crypto projection, so
        // its rows cannot be shown safely — refuse rather than emit unguarded values.
        IDbTable metaTable;
        try
        {
            metaTable = context.Model.GetTableFromDbName(config.TableSchema, config.MetaTableDbName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new BifrostExecutionError(
                $"The EAV meta table for '{context.Table.GraphQlName}' is not present in the model.");
        }

        // The FULL read chain for the meta table, not just its row filter. `_meta`
        // previously applied only IFilterTransformers.GetCombinedFilter, so:
        //   - the key/value columns never met IColumnReadGuard — a policy-denied
        //     value column was serialized into _meta in full, while the same
        //     caller's ordinary query of the meta table is rejected;
        //   - the FK column never met IColumnFilterGuard though it is the WHERE
        //     predicate of this read;
        //   - there was no CryptoReadProjector, so an envelope-encrypted value
        //     column went out as RAW CIPHERTEXT inside the _meta JSON.
        // TableReadChain is the single seam that applies all of it; guard decisions
        // stay with the guards (protocol-adapter-security invariant 4). One chain
        // serves the whole page.
        var readChain = await TableReadChain.ForAsync(
            context.Services, context.Model, metaTable, context.UserContext,
            ReadProjection.Client, QueryType.Standard, metaTable.GraphQlName, isNestedQuery: true);

        // `_meta` IS a client selection (the caller asked for the field), so a denied
        // column aborts rather than being silently dropped — the same reject semantics
        // the ordinary query path uses. The FK is the read's WHERE predicate and so
        // must clear the filter guard too. `_metaTruncated` describes the same attribute
        // set, so it clears the same guards.
        readChain.AssertReadable(new[] { config.KeyColumn, config.ValueColumn });
        readChain.AssertPredicateColumns(new[] { config.ForeignKeyColumn });

        var dialect = context.ConnFactory.Dialect;
        var securityParams = new SqlParameterCollection();
        var securityWhere = "";
        var rowFilter = readChain.RowFilter;
        if (rowFilter != null)
        {
            // Spliced into an existing WHERE, so a relationship-shaped filter (one
            // that needs an INNER JOIN) cannot be honoured; refuse it rather than
            // emit "AND (INNER JOIN ...)" and let the database reject it.
            var parts = rowFilter.RenderParts(context.Model, dialect, securityParams, alias: metaTable.DbName);
            if (!string.IsNullOrWhiteSpace(parts.Joins))
                throw new BifrostExecutionError(
                    "EAV _meta cannot apply a row-scope filter that requires a relationship join.");
            securityWhere = parts.Where;
        }

        return new EavMetaRead(config, metaTable, readChain, dialect, securityParams, securityWhere, rowKeys, distinctKeys);
    }

    /// <summary>
    /// Binds one chunk of parent keys (at most <see cref="ParentKey.MaxKeysPerQuery"/>) and the
    /// row-scope parameters onto <paramref name="command"/>, and returns the WHERE body
    /// (<c>fk IN (...)</c>, AND-ed with the row-scope filter) without the <c>WHERE</c> keyword.
    /// </summary>
    public string BindChunk(DbCommand command, IReadOnlyList<object> chunk)
    {
        var fkDataType = MetaTable.ColumnLookup[Config.ForeignKeyColumn].DataType;
        var placeholders = new string[chunk.Count];
        for (var i = 0; i < chunk.Count; i++)
        {
            var name = $"{Dialect.ParameterPrefix}pk{i}";
            placeholders[i] = DbParameterBinder.ComparisonPlaceholder(Dialect, name, fkDataType, chunk[i]);
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            DbParameterBinder.SetValue(parameter, chunk[i]);
            command.Parameters.Add(parameter);
        }
        foreach (var securityParameter in SecurityParams.Parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = securityParameter.Name;
            DbParameterBinder.SetValue(p, securityParameter.Value);
            command.Parameters.Add(p);
        }

        var keyPredicate = $"{FkCol} IN ({string.Join(",", placeholders)})";
        return string.IsNullOrEmpty(SecurityWhere)
            ? keyPredicate
            : $"{keyPredicate} AND ({SecurityWhere})";
    }

    /// <summary>The page's distinct parent keys in chunks of <see cref="ParentKey.MaxKeysPerQuery"/>.</summary>
    public IEnumerable<List<object>> KeyChunks()
    {
        for (var offset = 0; offset < DistinctKeys.Count; offset += ParentKey.MaxKeysPerQuery)
            yield return DistinctKeys.Skip(offset).Take(ParentKey.MaxKeysPerQuery).ToList();
    }

    /// <summary>
    /// The per-row entry point for a batch provider: a one-row page through
    /// <see cref="IBatchComputedColumnProvider.ComputeBatchAsync"/>.
    /// </summary>
    public static async ValueTask<object?> ComputeOneAsync(
        IBatchComputedColumnProvider provider, ComputedColumnContext context, CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var values = await provider.ComputeBatchAsync(new ComputedColumnBatchContext
        {
            Model = context.Model,
            Table = context.Table,
            Column = context.Column,
            Rows = new[] { context.Row },
            UserContext = context.UserContext,
            Services = context.Services,
            ConnFactory = context.ConnFactory,
        }, cancellationToken);
        return values[0];
    }

    private static object? ReadKeyValue(IReadOnlyDictionary<string, object?> row, ColumnDto keyColumn)
    {
        // The synthesized definition declares the PK (DB name) as its only
        // dependency, so the projected row is keyed by that name. Fall back to
        // the GraphQL name defensively (mirrors StateMachineTransitionsProvider).
        if (row.TryGetValue(keyColumn.DbName, out var byDb) && byDb is not null)
            return byDb;

        if (row.TryGetValue(keyColumn.GraphQlName, out var byGraphQl) && byGraphQl is not null)
            return byGraphQl;

        return null;
    }
}

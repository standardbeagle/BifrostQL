using BifrostQL.Core.Model;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Pure argument-shaping logic split out of <see cref="DbTableMutateResolver"/>:
    /// turns a mutation's raw argument dictionary (and an optional positional
    /// <c>_primaryKey</c>) into the (all-data, key-data, standard-data) triple the
    /// insert/update/delete paths consume. Free of GraphQL context and database
    /// access so it is directly unit-testable — the UPDATE/DELETE key/standard split
    /// that previously had only integration coverage.
    /// </summary>
    public static class MutationArgumentBinder
    {
        /// <summary>
        /// Splits <paramref name="baseData"/> into:
        /// <list type="bullet">
        /// <item><c>keyData</c> — primary-key columns, keyed by DATABASE column name
        /// (drives WHERE clauses / current-row loads, which are pure DB-name space).</item>
        /// <item><c>standardData</c> — non-key columns as the client addressed them
        /// (GraphQL field names). It only answers "is there anything to SET?"; the
        /// real SET list is taken from the transformer chain's output, which is
        /// rekeyed to DB column names once on the way in (see
        /// <see cref="Modules.IMutationTransformers"/>).</item>
        /// <item><c>data</c> — the union, with key data (DB-named) overlaid on the
        /// standard data.</item>
        /// </list>
        /// When <paramref name="primaryKeyValues"/> is supplied it wins over any
        /// PK columns present in <paramref name="baseData"/> (see <see cref="ResolvePrimaryKey"/>).
        /// </summary>
        public static (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData)
            SplitProperties(IDbTable table, IReadOnlyDictionary<string, object?> baseData, IReadOnlyList<object?>? primaryKeyValues)
        {
            var data = new Dictionary<string, object?>(baseData, StringComparer.OrdinalIgnoreCase);

            var pkKeyData = ResolvePrimaryKey(table, primaryKeyValues);
            Dictionary<string, object?> keyData;
            if (pkKeyData != null)
            {
                keyData = pkKeyData;
            }
            else
            {
                keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in data.Where(d => DbParameterBinder.IsPrimaryKeyColumn(table, d.Key)))
                    keyData[DbParameterBinder.ToDbColumnName(table, d.Key)] = d.Value;
            }

            var standardData = data
                .Where(d => !DbParameterBinder.IsPrimaryKeyColumn(table, d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var allData = new Dictionary<string, object?>(standardData, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in keyData)
                allData[kv.Key] = kv.Value;

            return (allData, keyData, standardData);
        }

        public static (Dictionary<string, object?> keyData, Dictionary<string, object?> setData)
            SplitKeyAndSet(IDbTable table, IReadOnlyDictionary<string, object?> dbData)
        {
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dbData.Where(d => DbParameterBinder.IsPrimaryKeyColumn(table, d.Key)))
                keyData[DbParameterBinder.ToDbColumnName(table, d.Key)] = d.Value;
            var setData = dbData
                .Where(d => !DbParameterBinder.IsPrimaryKeyColumn(table, d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return (keyData, setData);
        }

        /// <summary>
        /// Zips a positional <c>_primaryKey</c> argument against the table's key
        /// columns (in declared order), producing a DB-column-name-keyed dictionary.
        /// Returns null when <paramref name="primaryKeyValues"/> is null or empty.
        /// Throws <see cref="BifrostExecutionError"/> when the table has no primary key
        /// or the value count does not match the key-column count — the composite-PK
        /// arity guard the edit-db client relies on.
        /// </summary>
        public static Dictionary<string, object?>? ResolvePrimaryKey(IDbTable table, IReadOnlyList<object?>? primaryKeyValues)
        {
            if (primaryKeyValues == null || primaryKeyValues.Count == 0)
                return null;

            var keyColumns = table.KeyColumns.ToList();

            if (keyColumns.Count == 0)
                throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key columns.");

            if (primaryKeyValues.Count != keyColumns.Count)
                throw new BifrostExecutionError(
                    $"_primaryKey for '{table.DbName}' expects {keyColumns.Count} value(s) " +
                    $"({string.Join(", ", keyColumns.Select(c => c.GraphQlName))}) but received {primaryKeyValues.Count}.");

            return keyColumns.Zip(primaryKeyValues, (col, val) => new { col.ColumnName, Value = val })
                .ToDictionary(x => x.ColumnName, x => x.Value);
        }

        /// <summary>
        /// <see cref="ErrorCode"/> for a write whose primary key is incomplete.
        /// </summary>
        public const string PartialPrimaryKeyCode = "PARTIAL_PRIMARY_KEY";

        /// <summary>
        /// Rejects a PARTIAL primary key as a write predicate. A key column set that
        /// is missing any of the table's key columns does not address a row: on a
        /// composite key <c>(id, region)</c> a predicate built from <c>region</c>
        /// alone spans every row sharing that region, so an update rewrites them all
        /// and a delete removes them all — a bulk write wearing the shape of a
        /// single-row one. The GraphQL front door types its update/delete inputs with
        /// the key columns required, but the pipelines are also reached by
        /// <see cref="IMutationIntentExecutor"/>, whose data comes straight off an
        /// adapter's wire, so the invariant belongs here, where the predicate is
        /// built, rather than in one front door.
        ///
        /// <para>Supplying NO key column is a different case and stays allowed: an
        /// update with no key affects nothing, and a delete addressed purely by
        /// non-key predicate columns is the pipeline's documented filtered delete
        /// (<see cref="TableMutationPipeline.SelectPredicateColumns"/>).</para>
        ///
        /// <para>Presence is decided by COLUMN PRESENCE in
        /// <paramref name="suppliedDbColumns"/> — never by a value's truthiness — so a
        /// key value of <c>0</c>, an empty string, or null counts as supplied.
        /// Columns are database-named, matched case-insensitively. The message carries counts only,
        /// never the model's key-column names: it answers callers who may hold no
        /// read access to the table (see
        /// <c>.claude/rules/protocol-adapter-security.md</c> invariant 3).</para>
        /// </summary>
        public static void RequireCompleteKey(
            IDbTable table, IEnumerable<string> suppliedDbColumns, string operation)
        {
            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count <= 1)
                return;

            var predicateColumns = new HashSet<string>(suppliedDbColumns, StringComparer.OrdinalIgnoreCase);
            var supplied = keyColumns.Count(c => predicateColumns.Contains(c.ColumnName));
            if (supplied == 0 || supplied == keyColumns.Count)
                return;

            throw new BifrostExecutionError(
                $"{operation} of '{table.TableSchema}.{table.DbName}' supplied {supplied} primary-key column value(s) " +
                $"but {keyColumns.Count} are expected. A partial primary key does not address a row.")
            { ErrorCode = PartialPrimaryKeyCode };
        }
    }
}

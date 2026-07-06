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
        /// <item><c>standardData</c> — non-key columns, keeping their GraphQL field
        /// names so mutation transformers (e.g. enum-name mapping) still resolve
        /// columns by GraphQlName; normalized to DB names just before SQL generation.</item>
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
    }
}

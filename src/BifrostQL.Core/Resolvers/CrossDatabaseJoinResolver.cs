using System;
using System.Collections.Generic;
using System.Linq;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Specifies the type of in-memory join operation to perform when combining
    /// results from two different databases.
    /// </summary>
    public enum CrossJoinType
    {
        /// <summary>Only rows with matching keys in both sides are included.</summary>
        Inner,

        /// <summary>All left rows are included; unmatched right rows produce nulls.</summary>
        Left,
    }

    /// <summary>
    /// Performs in-memory joins between query results from different databases.
    /// Cross-database joins cannot be executed as SQL joins because the data lives
    /// in separate database servers. Instead, each database is queried independently
    /// and results are matched by join keys in memory.
    ///
    /// Performance characteristics:
    /// - Memory: O(left.Count + right.Count) for the lookup dictionary.
    /// - Time: O(left.Count + right.Count) for building and probing the hash table.
    /// - For large result sets, apply filters and pagination at the database level
    ///   before performing the cross-database join to limit memory usage.
    /// - N+1 is avoided by collecting all join keys from the left side in a single
    ///   pass, then fetching all matching right-side rows in one batched query.
    /// </summary>
    public static class CrossDatabaseJoinResolver
    {
        /// <summary>
        /// Joins two collections of dictionaries by matching a key column from each side.
        /// Each dictionary represents a row where keys are column names and values are
        /// the column values.
        /// </summary>
        /// <param name="leftRows">Rows from the source database.</param>
        /// <param name="leftKeyColumn">Column name in leftRows to use as the join key.</param>
        /// <param name="rightRows">Rows from the target database.</param>
        /// <param name="rightKeyColumn">Column name in rightRows to use as the join key.</param>
        /// <param name="rightPrefix">
        /// Prefix added to right-side column names in the merged result to avoid
        /// name collisions (e.g., "orderDb_").
        /// </param>
        /// <param name="joinType">The type of join: Inner or Left.</param>
        /// <returns>Merged rows with columns from both sides.</returns>
        public static IReadOnlyList<Dictionary<string, object?>> Join(
            IReadOnlyList<Dictionary<string, object?>> leftRows,
            string leftKeyColumn,
            IReadOnlyList<Dictionary<string, object?>> rightRows,
            string rightKeyColumn,
            string rightPrefix,
            CrossJoinType joinType = CrossJoinType.Inner)
        {
            if (leftRows.Count == 0)
                return Array.Empty<Dictionary<string, object?>>();

            if (rightRows.Count == 0)
            {
                if (joinType == CrossJoinType.Left)
                    return leftRows.Select(left => new Dictionary<string, object?>(left)).ToList();
                return Array.Empty<Dictionary<string, object?>>();
            }

            var rightLookup = BuildLookup(rightRows, rightKeyColumn);
            var rightColumnNames = rightRows[0].Keys
                .Select(k => (Original: k, Prefixed: $"{rightPrefix}{k}"))
                .ToList();

            var results = new List<Dictionary<string, object?>>();

            foreach (var left in leftRows)
            {
                if (!left.TryGetValue(leftKeyColumn, out var keyValue) || keyValue == null)
                {
                    if (joinType == CrossJoinType.Left)
                        results.Add(CreateLeftOnlyRow(left, rightColumnNames));
                    continue;
                }

                var normalizedKey = NormalizeKey(keyValue);
                if (rightLookup.TryGetValue(normalizedKey, out var matchingRightRows))
                {
                    foreach (var right in matchingRightRows)
                    {
                        var merged = new Dictionary<string, object?>(left);
                        foreach (var (original, prefixed) in rightColumnNames)
                        {
                            merged[prefixed] = right.TryGetValue(original, out var val) ? val : null;
                        }
                        results.Add(merged);
                    }
                }
                else if (joinType == CrossJoinType.Left)
                {
                    results.Add(CreateLeftOnlyRow(left, rightColumnNames));
                }
            }

            return results;
        }

        /// <summary>
        /// Collects all distinct non-null join key values from the specified column
        /// across all rows. Use this to batch-fetch matching rows from the target database
        /// instead of querying row-by-row (N+1 prevention).
        /// </summary>
        public static IReadOnlyList<object> CollectJoinKeys(
            IReadOnlyList<Dictionary<string, object?>> rows,
            string keyColumn)
        {
            var keys = new HashSet<string>();
            var result = new List<object>();

            foreach (var row in rows)
            {
                if (!row.TryGetValue(keyColumn, out var value) || value == null)
                    continue;

                var normalized = NormalizeKey(value);
                if (keys.Add(normalized))
                    result.Add(value);
            }

            return result;
        }

        private static Dictionary<string, List<Dictionary<string, object?>>> BuildLookup(
            IReadOnlyList<Dictionary<string, object?>> rows,
            string keyColumn)
        {
            var lookup = new Dictionary<string, List<Dictionary<string, object?>>>();

            foreach (var row in rows)
            {
                if (!row.TryGetValue(keyColumn, out var keyValue) || keyValue == null)
                    continue;

                var normalizedKey = NormalizeKey(keyValue);
                if (!lookup.TryGetValue(normalizedKey, out var list))
                {
                    list = new List<Dictionary<string, object?>>();
                    lookup[normalizedKey] = list;
                }
                list.Add(row);
            }

            return lookup;
        }

        private static Dictionary<string, object?> CreateLeftOnlyRow(
            Dictionary<string, object?> left,
            List<(string Original, string Prefixed)> rightColumnNames)
        {
            var row = new Dictionary<string, object?>(left);
            foreach (var (_, prefixed) in rightColumnNames)
            {
                row[prefixed] = null;
            }
            return row;
        }

        private static string NormalizeKey(object value)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Detects common prefixes in column names within a table.
    /// Used to identify logical column groupings and improve GraphQL naming.
    /// </summary>
    public sealed class ColumnPrefixDetector
    {
        /// <summary>
        /// Minimum number of columns that must share a prefix for it to be considered a valid group.
        /// </summary>
        public const int DefaultMinGroupSize = 3;

        /// <summary>
        /// Detects common column prefixes within a single table's columns.
        /// </summary>
        /// <param name="columns">The columns to analyze</param>
        /// <param name="minGroupSize">Minimum columns required to form a prefix group (default: 3)</param>
        /// <returns>List of detected column prefix groups, ordered by specificity (longer prefixes first)</returns>
        public static List<ColumnPrefixGroup> DetectColumnPrefixes(
            IEnumerable<ColumnDto> columns, 
            int minGroupSize = DefaultMinGroupSize)
        {
            var columnList = columns.ToList();
            var prefixBuckets = new Dictionary<string, List<ColumnDto>>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in columnList)
            {
                var name = column.ColumnName;
                var idx = name.IndexOf('_');
                
                // Collect all possible prefixes (e.g., "user_id" -> "user_", "user")
                while (idx > 0 && idx < name.Length - 1)
                {
                    var prefix = name.Substring(0, idx + 1); // includes trailing '_'
                    if (!prefixBuckets.TryGetValue(prefix, out var bucket))
                    {
                        bucket = new List<ColumnDto>();
                        prefixBuckets[prefix] = bucket;
                    }
                    if (!bucket.Contains(column))
                    {
                        bucket.Add(column);
                    }
                    
                    // Also add version without trailing underscore
                    var prefixWithoutUnderscore = prefix.TrimEnd('_');
                    if (prefixWithoutUnderscore.Length > 0 && prefixWithoutUnderscore != prefix)
                    {
                        if (!prefixBuckets.TryGetValue(prefixWithoutUnderscore, out var bucketNoUnderscore))
                        {
                            bucketNoUnderscore = new List<ColumnDto>();
                            prefixBuckets[prefixWithoutUnderscore] = bucketNoUnderscore;
                        }
                        if (!bucketNoUnderscore.Contains(column))
                        {
                            bucketNoUnderscore.Add(column);
                        }
                    }
                    
                    idx = name.IndexOf('_', idx + 1);
                }
            }

            return prefixBuckets
                .Where(kvp => kvp.Value.Count >= minGroupSize)
                .OrderByDescending(kvp => kvp.Key.Length)
                .ThenByDescending(kvp => kvp.Value.Count)
                .Select(kvp => new ColumnPrefixGroup(
                    kvp.Key,
                    kvp.Key.TrimEnd('_'),
                    kvp.Value.ToList()))
                .ToList();
        }

        /// <summary>
        /// Detects column prefixes for all tables in a model.
        /// </summary>
        /// <param name="tables">The tables to analyze</param>
        /// <param name="minGroupSize">Minimum columns required to form a prefix group</param>
        /// <returns>Dictionary mapping table DB names to their detected prefix groups</returns>
        public static Dictionary<string, List<ColumnPrefixGroup>> DetectPrefixesForTables(
            IEnumerable<IDbTable> tables,
            int minGroupSize = DefaultMinGroupSize)
        {
            var result = new Dictionary<string, List<ColumnPrefixGroup>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var table in tables)
            {
                var groups = DetectColumnPrefixes(table.Columns, minGroupSize);
                if (groups.Count > 0)
                {
                    result[table.DbName] = groups;
                }
            }
            
            return result;
        }

        /// <summary>
        /// Strips a prefix from a column name if it matches.
        /// </summary>
        /// <param name="columnName">The original column name</param>
        /// <param name="prefix">The prefix to strip (may or may not include trailing underscore)</param>
        /// <returns>The column name with prefix removed, or original if prefix doesn't match</returns>
        public static string StripPrefix(string columnName, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return columnName;

            // Normalize prefix to include underscore
            var normalizedPrefix = prefix.EndsWith('_') ? prefix : prefix + "_";
            
            if (columnName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return columnName.Substring(normalizedPrefix.Length);
            }
            
            // Try without underscore as fallback
            if (columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var result = columnName.Substring(prefix.Length);
                // If result starts with underscore, strip it
                if (result.StartsWith("_"))
                {
                    return result.Substring(1);
                }
                return result;
            }
            
            return columnName;
        }

        /// <summary>
        /// Checks if a column name has a specific prefix.
        /// </summary>
        public static bool HasPrefix(string columnName, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return false;

            var normalizedPrefix = prefix.EndsWith('_') ? prefix : prefix + "_";
            return columnName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the prefix of a column name if it matches any of the known prefixes.
        /// </summary>
        public static string? GetMatchingPrefix(string columnName, IEnumerable<string> prefixes)
        {
            foreach (var prefix in prefixes.OrderByDescending(p => p.Length))
            {
                if (HasPrefix(columnName, prefix))
                    return prefix;
            }
            return null;
        }
    }

    /// <summary>
    /// Represents a group of columns that share a common prefix.
    /// </summary>
    public sealed class ColumnPrefixGroup
    {
        /// <summary>
        /// The prefix including trailing underscore (e.g., "user_")
        /// </summary>
        public string Prefix { get; }

        /// <summary>
        /// The prefix without trailing underscore (e.g., "user")
        /// </summary>
        public string GroupName { get; }

        /// <summary>
        /// The columns that share this prefix
        /// </summary>
        public IReadOnlyList<ColumnDto> Columns { get; }

        /// <summary>
        /// Number of columns in this group
        /// </summary>
        public int Count => Columns.Count;

        public ColumnPrefixGroup(string prefix, string groupName, IReadOnlyList<ColumnDto> columns)
        {
            Prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
            GroupName = groupName ?? throw new ArgumentNullException(nameof(groupName));
            Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        }

        /// <summary>
        /// Checks if a column name belongs to this group.
        /// </summary>
        public bool ContainsColumn(string columnName)
        {
            return Columns.Any(c => 
                string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets the column names with the prefix stripped.
        /// </summary>
        public IEnumerable<string> GetStrippedColumnNames()
        {
            return Columns.Select(c => ColumnPrefixDetector.StripPrefix(c.ColumnName, Prefix));
        }

        public override string ToString()
        {
            return $"{Prefix} ({Count} columns: {string.Join(", ", Columns.Select(c => c.ColumnName))})";
        }
    }
}

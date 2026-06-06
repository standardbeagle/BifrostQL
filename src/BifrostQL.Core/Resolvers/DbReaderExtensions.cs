using System.Collections.Generic;
using System.Data.Common;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Shared helpers for projecting a <see cref="DbDataReader"/> row into a
    /// dictionary, so the per-column read loop lives in one place instead of
    /// being copied across the query/resolver executors.
    /// </summary>
    internal static class DbReaderExtensions
    {
        /// <summary>
        /// Reads the reader's current row into a <c>columnName → value</c> map,
        /// mapping <see cref="System.DBNull"/> to <c>null</c>. Pass a
        /// <paramref name="comparer"/> (e.g. <see cref="System.StringComparer.OrdinalIgnoreCase"/>)
        /// when case-insensitive column lookup is required; otherwise the default
        /// comparer is used.
        /// </summary>
        public static Dictionary<string, object?> ReadRow(DbDataReader reader, IEqualityComparer<string>? comparer = null)
        {
            var row = new Dictionary<string, object?>(comparer);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == System.DBNull.Value ? null : value;
            }
            return row;
        }
    }
}

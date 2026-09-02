using System;
using System.Collections.Generic;
using System.Linq;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Single definition of "which columns are blind-index shadow columns" for a
    /// table, shared by every surface that must treat them as server-derived:
    /// schema emission and the write path exclude them from inputs, the model
    /// build hides them from read schemas, the introspection funnel omits them,
    /// and the query guards reject direct references. One resolver so the
    /// surfaces cannot drift.
    /// </summary>
    public static class BlindIndexColumns
    {
        /// <summary>
        /// The DB names of the blind-index sibling columns declared (via
        /// <see cref="MetadataKeys.Crypto.BlindIndex"/>) by this table's encrypted
        /// columns. Case-insensitive; empty when the table has none.
        /// </summary>
        public static HashSet<string> TargetsOf(IDbTable table) =>
            TargetsOf(table.Columns);

        /// <summary>Overload for pre-model construction (raw column lists).</summary>
        public static HashSet<string> TargetsOf(IEnumerable<ColumnDto> columns) => columns
            .Select(c => c.GetMetadataValue(MetadataKeys.Crypto.BlindIndex))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

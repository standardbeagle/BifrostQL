using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Names relationship fields. A table's link fields share one GraphQL field namespace with its
    /// columns and computed columns, so a link named after the related table (<c>invoice.customer</c>
    /// next to a <c>customer</c> FK column) would emit the field twice and break the schema build.
    /// Every relationship strategy names its links here, and the name it picks is both the link's
    /// dictionary key and the field the schema emits.
    /// </summary>
    internal static class LinkFieldNames
    {
        /// <summary><paramref name="baseName"/>, or the first free <c>{baseName}_{n}</c> from n = 2.</summary>
        public static string Unique(IDbTable table, string baseName)
        {
            var computed = ComputedFieldNames(table);
            var name = baseName;
            var i = 2;
            while (IsTaken(table, computed, name))
                name = $"{baseName}_{i++}";
            return name;
        }

        /// <summary>
        /// The parent's collection field for <paramref name="childTable"/>: the child's name, or
        /// <c>{child}_children</c> for a self-reference, made unique on the parent.
        /// </summary>
        public static string ChildCollection(IDbTable parentTable, IDbTable childTable) =>
            Unique(parentTable,
                string.Equals(parentTable.GraphQlName, childTable.GraphQlName, StringComparison.OrdinalIgnoreCase)
                    ? $"{childTable.GraphQlName}_children"
                    : childTable.GraphQlName);

        // A column, computed column or any kind of link. Case-insensitive, as the link
        // dictionaries are.
        private static bool IsTaken(IDbTable table, IReadOnlySet<string> computed, string name) =>
            table.SingleLinks.ContainsKey(name)
            || table.MultiLinks.ContainsKey(name)
            || table.ManyToManyLinks.ContainsKey(name)
            || computed.Contains(name)
            || table.Columns.Any(c => string.Equals(c.GraphQlName, name, StringComparison.OrdinalIgnoreCase));

        // Computed columns declared in table metadata. Malformed computed metadata is reported by
        // ModelConfigValidator; naming a link must not throw past it.
        private static IReadOnlySet<string> ComputedFieldNames(IDbTable table)
        {
            try
            {
                return ComputedColumnConfigCollector.FromTable(table)
                    .Select(c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Resolvers.BifrostExecutionError)
            {
                return new HashSet<string>();
            }
        }
    }
}

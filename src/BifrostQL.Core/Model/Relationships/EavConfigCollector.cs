using BifrostQL.Core.Model.AppSchema;

namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Collects EAV (Entity-Attribute-Value) configurations from table metadata.
    /// EAV tables have metadata keys: eav-parent, eav-fk, eav-key, eav-value
    /// </summary>
    public sealed class EavConfigCollector
    {
        /// <summary>
        /// Collects EAV configurations from tables that have the required metadata.
        /// </summary>
        /// <param name="tables">All tables in the model.</param>
        /// <returns>List of EAV configurations.</returns>
        public List<EavConfig> Collect(IReadOnlyCollection<IDbTable> tables)
        {
            var tablesByDbName = new HashSet<string>(
                tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);
            
            var configs = new List<EavConfig>();

            foreach (var table in tables)
            {
                var parent = table.GetMetadataValue(MetadataKeys.Eav.Parent);
                var fk = table.GetMetadataValue(MetadataKeys.Eav.ForeignKey);
                var key = table.GetMetadataValue(MetadataKeys.Eav.Key);
                var value = table.GetMetadataValue(MetadataKeys.Eav.Value);

                if (string.IsNullOrWhiteSpace(parent) ||
                    string.IsNullOrWhiteSpace(fk) ||
                    string.IsNullOrWhiteSpace(key) ||
                    string.IsNullOrWhiteSpace(value))
                    continue;

                // Resolve parent: try exact match first, then prefix-aware match
                var parentDbName = ResolveParentTableName(table.DbName, parent, tablesByDbName);

                if (parentDbName == null)
                    continue;

                configs.Add(new EavConfig(table.DbName, parentDbName, fk!, key!, value!));
            }

            return configs;
        }

        private string? ResolveParentTableName(
            string metaTableName, 
            string parentShortName, 
            HashSet<string> tablesByDbName)
        {
            // Try exact match first
            if (tablesByDbName.Contains(parentShortName))
                return parentShortName;

            // Try prefix-aware match
            var lastUnderscore = metaTableName.LastIndexOf('_');
            if (lastUnderscore <= 0)
                return null;

            var prefix = metaTableName[..lastUnderscore];
            
            // Try progressively shorter prefixes
            while (prefix.Length > 0)
            {
                var candidate = prefix + "_" + parentShortName;
                if (tablesByDbName.Contains(candidate))
                    return candidate;

                var idx = prefix.LastIndexOf('_');
                if (idx <= 0) 
                    break;
                
                prefix = prefix[..idx];
            }

            return null;
        }
    }
}

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

                // Metadata-driven only: eav-parent must name an existing table exactly.
                // No name-prefix inference — EAV participation is an explicit, declared
                // choice, never detected from a naming convention.
                if (!tablesByDbName.Contains(parent!))
                    continue;

                // Capture the meta table's own schema so a non-default-schema meta
                // table (e.g. app.wp_postmeta) round-trips correctly. Without this,
                // downstream SQL built off MetaTableDbName alone drops the schema
                // and looks the table up unqualified (see EavMetaProvider's
                // TableReference(null, config.MetaTableDbName) call, owned by
                // another agent — it must switch to config.TableSchema).
                configs.Add(new EavConfig(table.DbName, parent!, fk!, key!, value!, table.TableSchema));
            }

            return configs;
        }
    }
}

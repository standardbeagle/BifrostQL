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
            // Match the parent WITHIN the meta table's own schema. A DbName-only set
            // would bind app.settings' eav-parent to dbo.settings (or vice versa) —
            // wrong table, wrong security filter (cross-schema data exposure).
            var tablesBySchemaAndName = new HashSet<(string Schema, string DbName)>(
                tables.Select(t => (t.TableSchema, t.DbName)),
                new SchemaNameComparer());

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

                // Metadata-driven only: eav-parent must name an existing table exactly,
                // in the meta table's own schema. No name-prefix inference — EAV
                // participation is an explicit, declared choice, never detected from a
                // naming convention.
                if (!tablesBySchemaAndName.Contains((table.TableSchema, parent!)))
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

        private sealed class SchemaNameComparer : IEqualityComparer<(string Schema, string DbName)>
        {
            public bool Equals((string Schema, string DbName) x, (string Schema, string DbName) y) =>
                string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DbName, y.DbName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Schema, string DbName) obj) =>
                HashCode.Combine(
                    obj.Schema?.ToLowerInvariant(),
                    obj.DbName?.ToLowerInvariant());
        }
    }
}

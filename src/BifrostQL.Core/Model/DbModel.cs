using System.Data;
using System.Text;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel;
using Pluralize.NET.Core;
using static BifrostQL.Core.Schema.TableSchemaGenerator;

namespace BifrostQL.Core.Model
{

    public interface IDbModel
    {
        IReadOnlyCollection<IDbTable> Tables { get; }
        IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; }
        IDbTable GetTableByFullGraphQlName(string fullName);
        IDbTable GetTableFromDbName(string tableName);
        IDictionary<string, object?> Metadata { get; init; }
        string? GetMetadataValue(string property);
        bool GetMetadataBool(string property, bool defaultValue);

        /// <summary>
        /// The type mapper for converting database data types to GraphQL types.
        /// Defaults to SqlServerTypeMapper for backward compatibility.
        /// Dialect-specific implementations (PostgreSQL, MySQL, SQLite) override this
        /// to correctly map their native types.
        /// </summary>
        ITypeMapper TypeMapper => SqlServerTypeMapper.Instance;

        /// <summary>
        /// EAV (Entity-Attribute-Value) configurations linking meta tables to parent tables.
        /// When present, the parent table's GraphQL type gets a <c>_meta: String</c> field
        /// that returns all meta rows as a JSON object.
        /// </summary>
        IReadOnlyList<EavConfig> EavConfigs => Array.Empty<EavConfig>();
    }

    public sealed class DbModel : IDbModel
    {
        internal static readonly Pluralizer Pluralizer = new();
        public IReadOnlyCollection<IDbTable> Tables { get; init; } = null!;
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; init; } = Array.Empty<DbStoredProcedure>();
        public IDictionary<string, object?> Metadata { get; init; } = null!;
        public ITypeMapper TypeMapper { get; set; } = SqlServerTypeMapper.Instance;
        public IReadOnlyList<EavConfig> EavConfigs { get; set; } = Array.Empty<EavConfig>();
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => (!Metadata.TryGetValue(property, out var v) || v?.ToString() == null) ? defaultValue : v.ToString() == "true";
        public bool CompareMetadata(string property, string value)
        {
            if (!Metadata.TryGetValue(property, out var v)) return false;
            return string.Equals(v?.ToString(), value, StringComparison.InvariantCultureIgnoreCase);
        }

        /// <summary>
        /// Searches for the table by its full graphql name
        /// </summary>
        /// <param name="fullName"></param>
        /// <returns></returns>
        public IDbTable GetTableByFullGraphQlName(string fullName)
        {
            return Tables?.FirstOrDefault(t => t.MatchName(fullName)) ?? throw new ArgumentOutOfRangeException(nameof(fullName), fullName, $"failed table lookup on graphql name: {fullName}");
        }
        public IDbTable GetTableFromDbName(string tableName)
        {
            return Tables?.FirstOrDefault(t => string.Equals(t.DbName, tableName, StringComparison.InvariantCultureIgnoreCase)) ?? throw new ArgumentOutOfRangeException(nameof(tableName), tableName, $"failed table lookup on db name: {tableName}");
        }

        /// <summary>
        /// Collects EAV configurations from table metadata using the collector pattern.
        /// </summary>
        internal static List<EavConfig> CollectEavConfigs(IReadOnlyCollection<IDbTable> tables)
        {
            var collector = new EavConfigCollector();
            return collector.Collect(tables);
        }

        /// <summary>
        /// Links tables using the strategy pattern for relationship detection.
        /// Orchestrates foreign key, name-based, and many-to-many detection strategies.
        /// </summary>
        private void LinkTables(IReadOnlyCollection<DbForeignKey> foreignKeys, IReadOnlyList<PrefixGroup>? prefixGroups = null)
        {
            var orchestrator = new TableRelationshipOrchestrator();
            orchestrator.LinkTables(this, foreignKeys, prefixGroups ?? Array.Empty<PrefixGroup>());
        }

        private static void ApplyAdditionalMetadata(
            List<DbTable> tables,
            IDictionary<string, IDictionary<string, object?>> additionalMetadata)
        {
            foreach (var table in tables)
            {
                var qualifiedName = $"{table.TableSchema}.{table.DbName}";
                if (additionalMetadata.TryGetValue(qualifiedName, out var tableExtra))
                {
                    foreach (var (key, value) in tableExtra)
                    {
                        table.Metadata[key] = value;
                    }
                }
            }
        }

        /// <summary>
        /// Detects common table name prefixes. A prefix must end with '_' and be shared by at least
        /// <paramref name="minGroupSize"/> tables to form a valid group.
        /// </summary>
        internal static List<PrefixGroup> DetectPrefixGroups(IReadOnlyCollection<IDbTable> tables, int minGroupSize = 3)
        {
            var prefixBuckets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
            {
                var name = table.DbName;
                var idx = name.IndexOf('_');
                while (idx > 0 && idx < name.Length - 1)
                {
                    var prefix = name.Substring(0, idx + 1); // includes trailing '_'
                    if (!prefixBuckets.TryGetValue(prefix, out var bucket))
                    {
                        bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        prefixBuckets[prefix] = bucket;
                    }
                    bucket.Add(name);
                    idx = name.IndexOf('_', idx + 1);
                }
            }

            return prefixBuckets
                .Where(kvp => kvp.Value.Count >= minGroupSize)
                .OrderByDescending(kvp => kvp.Value.Count)
                .ThenBy(kvp => kvp.Key.Length)
                .Select(kvp => new PrefixGroup(
                    kvp.Key,
                    kvp.Key.TrimEnd('_'),
                    kvp.Value))
                .ToList();
        }

        /// <summary>
        /// Parses manually configured prefix groups from metadata value.
        /// Format: "wp_=wp, wp2_=wp2"
        /// </summary>
        internal static List<PrefixGroup> ParsePrefixGroups(string value, IReadOnlyCollection<IDbTable> tables)
        {
            var result = new List<PrefixGroup>();
            var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var entry in entries)
            {
                var eqIdx = entry.IndexOf('=');
                if (eqIdx <= 0) continue;

                var prefix = entry.Substring(0, eqIdx).Trim();
                var groupName = entry.Substring(eqIdx + 1).Trim();

                if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(groupName))
                    continue;

                // Ensure prefix ends with '_'
                if (!prefix.EndsWith('_'))
                    prefix += "_";

                var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in tables)
                {
                    if (table.DbName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        tableNames.Add(table.DbName);
                }

                if (tableNames.Count > 0)
                    result.Add(new PrefixGroup(prefix, groupName, tableNames));
            }

            return result;
        }

        public static DbModel FromTables(List<DbTable> tables, IMetadataLoader metadataLoader)
        {
            return FromTables(tables, metadataLoader, Array.Empty<DbStoredProcedure>());
        }

        public static DbModel FromTables(List<DbTable> tables, IMetadataLoader metadataLoader, IReadOnlyCollection<DbStoredProcedure> storedProcedures)
        {
            return FromTables(tables, metadataLoader, storedProcedures, Array.Empty<DbForeignKey>());
        }

        public static DbModel FromTables(List<DbTable> tables, IMetadataLoader metadataLoader, IReadOnlyCollection<DbStoredProcedure> storedProcedures, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            return FromTables(tables, metadataLoader, storedProcedures, foreignKeys, null);
        }

        public static DbModel FromTables(List<DbTable> tables, IMetadataLoader metadataLoader, IReadOnlyCollection<DbStoredProcedure> storedProcedures, IReadOnlyCollection<DbForeignKey> foreignKeys, IDictionary<string, IDictionary<string, object?>>? additionalMetadata)
        {
            foreach (var table in tables)
            {
                metadataLoader.ApplyTableMetadata(table, table.Metadata);
                foreach (var column in table.Columns)
                {
                    metadataLoader.ApplyColumnMetadata(table, column, column.Metadata);
                }
            }

            if (additionalMetadata != null)
            {
                ApplyAdditionalMetadata(tables, additionalMetadata);
            }

            var dbMetadata = new Dictionary<string, object?>();
            metadataLoader.ApplyDatabaseMetadata(dbMetadata);

            if (additionalMetadata != null &&
                additionalMetadata.TryGetValue(":root", out var rootMetadata))
            {
                foreach (var (key, value) in rootMetadata)
                {
                    dbMetadata[key] = value;
                }
            }

            var schemaPrefixOptions = SchemaPrefixOptions.FromMetadata(dbMetadata);
            var prefixedTables = tables.Select(t => t.WithSchemaPrefix(schemaPrefixOptions)).ToList();

            var includePattern = dbMetadata.TryGetValue("sp-include", out var inc) ? inc?.ToString() : null;
            var excludePattern = dbMetadata.TryGetValue("sp-exclude", out var exc) ? exc?.ToString() : null;

            var filteredProcs = storedProcedures
                .Where(p => DbStoredProcedure.MatchesFilter(p.DbName, includePattern, excludePattern))
                .ToList();

            var model =
                new DbModel()
                {
                    Tables = prefixedTables.Where(t => t.CompareMetadata("visibility", "hidden") == false).ToList(),
                    StoredProcedures = filteredProcs,
                    Metadata = dbMetadata,
                };

            // Detect or parse prefix groups for name-based linking
            List<PrefixGroup> prefixGroups;
            if (dbMetadata.TryGetValue("prefix-groups", out var pgValue) &&
                pgValue?.ToString() is { Length: > 0 } pgStr)
            {
                prefixGroups = ParsePrefixGroups(pgStr, model.Tables);
            }
            else
            {
                prefixGroups = DetectPrefixGroups(model.Tables);
            }

            // Run application schema detection (WordPress, Drupal, etc.)
            var existingSchemas = model.Tables.Select(t => t.TableSchema).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var appResult = AppSchema.AppSchemaDetectionService.Default.Detect(
                model.Tables.ToList(), dbMetadata, existingSchemas);

            var allForeignKeys = foreignKeys;
            if (appResult != null)
            {
                // Merge detected prefix groups with manually-configured ones
                var detectedPrefixNames = new HashSet<string>(
                    appResult.PrefixGroups.Select(pg => pg.Prefix), StringComparer.OrdinalIgnoreCase);
                foreach (var pg in prefixGroups)
                {
                    if (!detectedPrefixNames.Contains(pg.Prefix))
                        detectedPrefixNames.Add(pg.Prefix); // track for dedup
                    // manual groups always win — they were parsed/detected first
                }
                var mergedGroups = new List<PrefixGroup>(prefixGroups);
                foreach (var pg in appResult.PrefixGroups)
                {
                    if (!mergedGroups.Any(g => string.Equals(g.Prefix, pg.Prefix, StringComparison.OrdinalIgnoreCase)))
                        mergedGroups.Add(pg);
                }
                prefixGroups = mergedGroups;

                // Apply additional metadata from detector
                if (appResult.AdditionalMetadata.Count > 0)
                    ApplyAdditionalMetadata(prefixedTables, appResult.AdditionalMetadata);

                // Convert synthetic foreign keys to DbForeignKey objects
                if (appResult.ExplicitForeignKeys.Count > 0)
                {
                    var tableLookup = model.Tables.ToDictionary(t => t.DbName, t => t, StringComparer.OrdinalIgnoreCase);
                    var syntheticFks = new List<DbForeignKey>();
                    foreach (var sfk in appResult.ExplicitForeignKeys)
                    {
                        // Resolve table names — synthetic FKs use base names, try exact match first
                        if (!tableLookup.TryGetValue(sfk.ChildTable, out var childTable) ||
                            !tableLookup.TryGetValue(sfk.ParentTable, out var parentTable))
                            continue;

                        syntheticFks.Add(new DbForeignKey
                        {
                            ConstraintName = $"SFK_{sfk.ChildTable}_{sfk.ChildColumn}_{sfk.ParentTable}_{sfk.ParentColumn}",
                            ChildTableSchema = childTable.TableSchema,
                            ChildTableName = childTable.DbName,
                            ChildColumnNames = new[] { sfk.ChildColumn },
                            ParentTableSchema = parentTable.TableSchema,
                            ParentTableName = parentTable.DbName,
                            ParentColumnNames = new[] { sfk.ParentColumn },
                        });
                    }

                    if (syntheticFks.Count > 0)
                        allForeignKeys = foreignKeys.Concat(syntheticFks).ToList();
                }
            }

            model.EavConfigs = CollectEavConfigs(model.Tables);

            model.LinkTables(allForeignKeys, prefixGroups);
            return model;
        }
    }

    public record PrefixGroup(string Prefix, string GroupName, HashSet<string> TableDbNames);

    public enum MutateActions
    {
        Insert,
        Update,
        Delete,
        Upsert
    }

    public interface IDbSchema
    {
        public string DbName { get; }
        public string GraphQlName { get; }
    }

    public interface ISchemaNames : IDbSchema
    {
        public string NormalizedName { get; }
    }
    public interface IDbTable
    {
        /// <summary>
        /// The name of the table as it is in the database, includes spaces and special characters
        /// </summary>
        string DbName { get; init; }

        /// <summary>
        /// The name translated so that it can be used as a graphql identifier
        /// </summary>
        string GraphQlName { get; init; }

        /// <summary>
        /// The table name translated so that it can be used to predict matches from other tables and columns
        /// </summary>
        string NormalizedName { get; }

        /// <summary>
        /// The schema that the table belongs to using its database name
        /// </summary>
        string TableSchema { get; init; }

        /// <summary>
        /// The graphql name of the table, including the schema if it is not dbo
        /// </summary>
        string ColumnEnumTypeName { get; }
        string ColumnFilterTypeName { get; }
        string TableFilterTypeName { get; }
        string TableColumnSortEnumName { get; }
        string JoinFieldName { get; }
        string SingleFieldName { get; }
        string GetJoinTypeName(IDbTable joinTable);
        string AggregateValueTypeName { get; }

        string GetActionTypeName(MutateActions action);

        IEnumerable<ColumnDto> Columns { get; }
        IDictionary<string, ColumnDto> ColumnLookup { get; init; }
        IDictionary<string, ColumnDto> GraphQlLookup { get; init; }
        IDictionary<string, TableLinkDto> SingleLinks { get; init; }
        IDictionary<string, TableLinkDto> MultiLinks { get; init; }
        IDictionary<string, ManyToManyLink> ManyToManyLinks { get; init; }
        IEnumerable<ColumnDto> KeyColumns { get; }
        string DbTableRef { get; }

        bool MatchName(string fullName);

        IDictionary<string, object?> Metadata { get; init; }
        string? GetMetadataValue(string property);
        bool CompareMetadata(string property, string value);
    }


    public class TableLinkDto
    {
        public TableLinkDto() { }
        /// <summary>The name of the join in the scope of the table being linked from, it is context dependent. The ParentTable and ChildTable properties refer to the same tables from both sides of the link.</summary>
        public string Name { get; init; } = null!;
        /// <summary>Parent table always refers to the one in one to many relations in database joins</summary>
        public IDbTable ParentTable { get; init; } = null!;
        /// <summary>Child table always refers to the many in one to many relations in database joins</summary>
        public IDbTable ChildTable { get; init; } = null!;
        /// <summary>Parent id always refers to the one in one to many relations in database joins</summary>
        public ColumnDto ParentId { get; init; } = null!;
        /// <summary>Child id always refers to the many in one to many relations in database joins</summary>
        public ColumnDto ChildId { get; init; } = null!;

        public string GetSqlSourceTableRef(LinkDirection direction)
        {
            if (direction == LinkDirection.ManyToOne)
                return ChildTable.DbTableRef;
            return ParentTable.DbTableRef;
        }

        public string GetSqlDestTableRef(LinkDirection direction)
        {
            if (direction == LinkDirection.ManyToOne)
                return ParentTable.DbTableRef;
            return ChildTable.DbTableRef;
        }

        public string GetSqlDestJoinColumn(LinkDirection direction)
        {
            if (direction == LinkDirection.ManyToOne)
                return ParentId.DbName;
            return ChildId.DbName;
        }

        public string GetSqlSourceColumns(LinkDirection direction, string? tableName = null, string? columnName = null)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(tableName))
                builder.Append($"[{tableName}].");
            else if (direction == LinkDirection.ManyToOne)
                builder.Append($"[{ChildTable.DbName}].");
            else
                builder.Append($"[{ParentTable.DbName}].");

            if (direction == LinkDirection.ManyToOne)
                builder.Append($"[{ChildId.DbName}]");
            else
                builder.Append($"[{ParentId.DbName}]");

            if (!string.IsNullOrWhiteSpace(columnName))
                builder.Append($" AS [{columnName}]");

            return builder.ToString();
        }
        public override string ToString() => $"{Name}-[{ChildId.TableName}.{ChildId.ColumnName}={ParentId.TableName}.{ParentId.ColumnName}]";
    }

    /// <summary>
    /// Represents a many-to-many relationship between two tables through a junction table.
    /// The junction table is hidden from the GraphQL schema; queries through this link
    /// automatically join through the junction table.
    /// </summary>
    public sealed class ManyToManyLink
    {
        /// <summary>The GraphQL field name for this link on the source table.</summary>
        public string Name { get; init; } = null!;

        /// <summary>The source table (the table that exposes this M:N field).</summary>
        public IDbTable SourceTable { get; init; } = null!;

        /// <summary>The junction/bridge table that connects source and target.</summary>
        public IDbTable JunctionTable { get; init; } = null!;

        /// <summary>The target table (the table returned by the M:N field).</summary>
        public IDbTable TargetTable { get; init; } = null!;

        /// <summary>The column on the source table that joins to the junction table (typically the PK).</summary>
        public ColumnDto SourceColumn { get; init; } = null!;

        /// <summary>The column on the junction table that references the source table.</summary>
        public ColumnDto JunctionSourceColumn { get; init; } = null!;

        /// <summary>The column on the junction table that references the target table.</summary>
        public ColumnDto JunctionTargetColumn { get; init; } = null!;

        /// <summary>The column on the target table that the junction references (typically the PK).</summary>
        public ColumnDto TargetColumn { get; init; } = null!;

        public override string ToString() =>
            $"M:N[{SourceTable.DbName}.{SourceColumn.ColumnName} -> {JunctionTable.DbName}({JunctionSourceColumn.ColumnName},{JunctionTargetColumn.ColumnName}) -> {TargetTable.DbName}.{TargetColumn.ColumnName}]";
    }

    public record SchemaRef(string Catalog, string Schema);
    public record TableRef(string Catalog, string Schema, string Table)
        : SchemaRef(Catalog, Schema);
    public record ColumnRef(string Catalog, string Schema, string Table, string Column)
        : TableRef(Catalog, Schema, Table);


}

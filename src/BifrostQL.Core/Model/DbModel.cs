using System.Data;
using System.Text;
using BifrostQL.Core.Model.AppSchema;
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
        /// Collects EAV configurations from table metadata. A table with metadata keys
        /// eav-parent, eav-fk, eav-key, eav-value defines a meta table that should be
        /// flattened into a _meta JSON field on the parent entity.
        /// </summary>
        internal static List<EavConfig> CollectEavConfigs(IReadOnlyCollection<IDbTable> tables)
        {
            var tablesByDbName = new HashSet<string>(
                tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);
            var configs = new List<EavConfig>();

            foreach (var table in tables)
            {
                var parent = table.GetMetadataValue("eav-parent");
                var fk = table.GetMetadataValue("eav-fk");
                var key = table.GetMetadataValue("eav-key");
                var value = table.GetMetadataValue("eav-value");

                if (string.IsNullOrWhiteSpace(parent) ||
                    string.IsNullOrWhiteSpace(fk) ||
                    string.IsNullOrWhiteSpace(key) ||
                    string.IsNullOrWhiteSpace(value))
                    continue;

                // Resolve parent: try exact match first, then prefix-aware match
                var parentDbName = tablesByDbName.Contains(parent!) ? parent! : null;
                if (parentDbName == null)
                {
                    // The metadata might use a short name (e.g., "posts" when table is "wp_posts").
                    // Try to find by matching the suffix after the meta table's prefix.
                    var metaName = table.DbName;
                    var lastUnderscore = metaName.LastIndexOf('_');
                    if (lastUnderscore > 0)
                    {
                        var prefix = metaName[..lastUnderscore];
                        // Try progressively shorter prefixes
                        while (prefix.Length > 0)
                        {
                            var candidate = prefix + "_" + parent;
                            if (tablesByDbName.Contains(candidate))
                            {
                                parentDbName = candidate;
                                break;
                            }
                            var idx = prefix.LastIndexOf('_');
                            if (idx <= 0) break;
                            prefix = prefix[..idx];
                        }
                    }
                }

                if (parentDbName == null)
                    continue;

                configs.Add(new EavConfig(table.DbName, parentDbName, fk!, key!, value!));
            }

            return configs;
        }

        private void LinkTables(IReadOnlyCollection<DbForeignKey> foreignKeys, IReadOnlyList<PrefixGroup>? prefixGroups = null)
        {
            var tablesByDbName = this.Tables
                .ToDictionary(t => (Schema: t.TableSchema, Name: t.DbName),
                    t => t,
                    new SchemaTableComparer());

            var fkLinkedColumns = LinkTablesFromForeignKeys(foreignKeys, tablesByDbName);

            LinkTablesFromNames(fkLinkedColumns, prefixGroups ?? Array.Empty<PrefixGroup>());

            DetectManyToManyFromMetadata();
            AutoDetectManyToMany(foreignKeys, tablesByDbName);
        }

        private HashSet<(string TableSchema, string TableName, string ColumnName)> LinkTablesFromForeignKeys(
            IReadOnlyCollection<DbForeignKey> foreignKeys,
            Dictionary<(string Schema, string Name), IDbTable> tablesByDbName)
        {
            var linked = new HashSet<(string TableSchema, string TableName, string ColumnName)>(
                new SchemaTableColumnComparer());

            foreach (var fk in foreignKeys)
            {
                if (fk.IsComposite)
                    continue;

                if (!tablesByDbName.TryGetValue((fk.ChildTableSchema, fk.ChildTableName), out var childTable))
                    continue;
                if (!tablesByDbName.TryGetValue((fk.ParentTableSchema, fk.ParentTableName), out var parentTable))
                    continue;

                var childColumnName = fk.ChildColumnNames[0];
                var parentColumnName = fk.ParentColumnNames[0];

                if (!childTable.ColumnLookup.TryGetValue(childColumnName, out var childColumn))
                    continue;
                if (!parentTable.ColumnLookup.TryGetValue(parentColumnName, out var parentColumn))
                    continue;

                if (childTable.SingleLinks.ContainsKey(parentTable.GraphQlName))
                    continue;

                childTable.SingleLinks.Add(parentTable.GraphQlName,
                    new TableLinkDto
                    {
                        Name = parentTable.GraphQlName,
                        ChildId = childColumn,
                        ParentId = parentColumn,
                        ChildTable = childTable,
                        ParentTable = parentTable
                    });

                if (!parentTable.MultiLinks.ContainsKey(childTable.GraphQlName))
                {
                    parentTable.MultiLinks.Add(childTable.GraphQlName,
                        new TableLinkDto
                        {
                            Name = childTable.GraphQlName,
                            ChildId = childColumn,
                            ParentId = parentColumn,
                            ChildTable = childTable,
                            ParentTable = parentTable
                        });
                }

                linked.Add((fk.ChildTableSchema, fk.ChildTableName, childColumnName));
            }

            return linked;
        }

        private void LinkTablesFromNames(
            HashSet<(string TableSchema, string TableName, string ColumnName)> fkLinkedColumns,
            IReadOnlyList<PrefixGroup> prefixGroups)
        {
            // Global lookup: NormalizedName -> table (for non-prefixed matching)
            var singleTables = this.Tables
                .Where(t => t.KeyColumns.Count() == 1)
                .ToDictionary(t => t.NormalizedName, StringComparer.InvariantCultureIgnoreCase);

            // Per-prefix-group lookup: stripped+singularized name -> table
            // e.g. for prefix "wp_", table "wp_users" -> key "user"
            var prefixLookups = new Dictionary<string, Dictionary<string, IDbTable>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in prefixGroups)
            {
                var lookup = new Dictionary<string, IDbTable>(StringComparer.InvariantCultureIgnoreCase);
                foreach (var table in this.Tables)
                {
                    if (!group.TableDbNames.Contains(table.DbName))
                        continue;
                    if (table.KeyColumns.Count() != 1)
                        continue;

                    var stripped = table.DbName.Substring(group.Prefix.Length);
                    var normalized = Pluralizer.Singularize(stripped);
                    lookup.TryAdd(normalized, table);
                }
                prefixLookups[group.Prefix] = lookup;
            }

            // Build reverse index: table DbName -> prefix group prefix (first match wins)
            var tableToPrefixGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in prefixGroups)
            {
                foreach (var tableName in group.TableDbNames)
                {
                    tableToPrefixGroup.TryAdd(tableName, group.Prefix);
                }
            }

            // Find column-to-table matches
            IDbTable? FindParentTable(IDbTable ownerTable, ColumnDto column)
            {
                // If the column's table belongs to a prefix group, try the prefix-stripped lookup first
                if (tableToPrefixGroup.TryGetValue(ownerTable.DbName, out var prefix) &&
                    prefixLookups.TryGetValue(prefix, out var prefixLookup) &&
                    prefixLookup.TryGetValue(column.NormalizedName, out var prefixMatch))
                {
                    return prefixMatch;
                }

                // Fall back to global lookup
                if (singleTables.TryGetValue(column.NormalizedName, out var globalMatch))
                    return globalMatch;

                return null;
            }

            var idMatches = this.Tables
                .SelectMany(table => table.Columns.Select(column => (table, column)))
                .Select(c => (c.table, c.column, parent: FindParentTable(c.table, c.column)))
                .Where(c => c.parent != null)
                .Where(c => string.Equals(c.column.NormalizedName, c.table.NormalizedName,
                    StringComparison.InvariantCultureIgnoreCase) == false)
                .Where(c => !fkLinkedColumns.Contains((c.table.TableSchema, c.table.DbName, c.column.ColumnName)))
                .Where(c => c.column.DataType == c.parent!.KeyColumns.First().DataType)
                .Select(c => (c.column, c.table, parent: c.parent!))
                .ToArray();

            foreach (var idMatch in idMatches)
            {
                if (idMatch.table.SingleLinks.ContainsKey(idMatch.parent.GraphQlName))
                    continue;

                idMatch.table.SingleLinks.Add(idMatch.parent.GraphQlName,
                    new TableLinkDto
                    {
                        Name = idMatch.parent.GraphQlName,
                        ChildId = idMatch.column,
                        ParentId = idMatch.parent.KeyColumns.First(),
                        ChildTable = idMatch.table,
                        ParentTable = idMatch.parent
                    });

                if (!idMatch.parent.MultiLinks.ContainsKey(idMatch.table.GraphQlName))
                {
                    idMatch.parent.MultiLinks.Add(idMatch.table.GraphQlName,
                        new TableLinkDto
                        {
                            Name = idMatch.table.GraphQlName,
                            ChildId = idMatch.column,
                            ParentId = idMatch.parent.KeyColumns.First(),
                            ChildTable = idMatch.table,
                            ParentTable = idMatch.parent
                        });
                }
            }
        }

        /// <summary>
        /// Parses many-to-many metadata on tables. Format: "many-to-many: TargetTable:JunctionTable"
        /// Adds ManyToManyLink entries to both source and target tables.
        /// </summary>
        private void DetectManyToManyFromMetadata()
        {
            var tablesByGraphQl = this.Tables
                .ToDictionary(t => t.GraphQlName, t => t, StringComparer.InvariantCultureIgnoreCase);

            foreach (var sourceTable in this.Tables)
            {
                var m2mValue = sourceTable.GetMetadataValue("many-to-many");
                if (string.IsNullOrWhiteSpace(m2mValue))
                    continue;

                var entries = m2mValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var entry in entries)
                {
                    var parts = entry.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                        continue;

                    var targetName = parts[0];
                    var junctionName = parts[1];

                    if (!tablesByGraphQl.TryGetValue(targetName, out var targetTable))
                        continue;
                    if (!tablesByGraphQl.TryGetValue(junctionName, out var junctionTable))
                        continue;

                    if (!TryFindJunctionColumns(sourceTable, junctionTable, targetTable,
                            out var sourceCol, out var junctionSourceCol, out var junctionTargetCol, out var targetCol))
                        continue;

                    AddManyToManyLink(sourceTable, junctionTable, targetTable,
                        sourceCol, junctionSourceCol, junctionTargetCol, targetCol);
                }
            }
        }

        /// <summary>
        /// Auto-detects junction tables: tables with exactly 2 FK columns pointing to other tables
        /// and no additional non-key data columns (only PK columns and the 2 FK columns).
        /// </summary>
        private void AutoDetectManyToMany(
            IReadOnlyCollection<DbForeignKey> foreignKeys,
            Dictionary<(string Schema, string Name), IDbTable> tablesByDbName)
        {
            var fksByChild = foreignKeys
                .Where(fk => !fk.IsComposite)
                .GroupBy(fk => (fk.ChildTableSchema, fk.ChildTableName), new SchemaTableComparer())
                .ToDictionary(g => g.Key, g => g.ToList(), new SchemaTableComparer());

            foreach (var kvp in fksByChild)
            {
                var fks = kvp.Value;
                if (fks.Count != 2)
                    continue;

                if (!tablesByDbName.TryGetValue(kvp.Key, out var junctionTable))
                    continue;

                var nonKeyColumns = junctionTable.Columns
                    .Where(c => !c.IsPrimaryKey)
                    .Where(c => !string.Equals(c.ColumnName, fks[0].ChildColumnNames[0], StringComparison.OrdinalIgnoreCase))
                    .Where(c => !string.Equals(c.ColumnName, fks[1].ChildColumnNames[0], StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (nonKeyColumns.Count > 0)
                    continue;

                if (!tablesByDbName.TryGetValue((fks[0].ParentTableSchema, fks[0].ParentTableName), out var tableA))
                    continue;
                if (!tablesByDbName.TryGetValue((fks[1].ParentTableSchema, fks[1].ParentTableName), out var tableB))
                    continue;

                if (!junctionTable.ColumnLookup.TryGetValue(fks[0].ChildColumnNames[0], out var junctionColA))
                    continue;
                if (!tableA.ColumnLookup.TryGetValue(fks[0].ParentColumnNames[0], out var parentColA))
                    continue;
                if (!junctionTable.ColumnLookup.TryGetValue(fks[1].ChildColumnNames[0], out var junctionColB))
                    continue;
                if (!tableB.ColumnLookup.TryGetValue(fks[1].ParentColumnNames[0], out var parentColB))
                    continue;

                // A -> junction -> B
                if (!tableA.ManyToManyLinks.ContainsKey(tableB.GraphQlName))
                {
                    AddManyToManyLink(tableA, junctionTable, tableB,
                        parentColA, junctionColA, junctionColB, parentColB);
                }

                // B -> junction -> A
                if (!tableB.ManyToManyLinks.ContainsKey(tableA.GraphQlName))
                {
                    AddManyToManyLink(tableB, junctionTable, tableA,
                        parentColB, junctionColB, junctionColA, parentColA);
                }
            }
        }

        private static bool TryFindJunctionColumns(
            IDbTable sourceTable, IDbTable junctionTable, IDbTable targetTable,
            out ColumnDto sourceCol, out ColumnDto junctionSourceCol,
            out ColumnDto junctionTargetCol, out ColumnDto targetCol)
        {
            sourceCol = null!;
            junctionSourceCol = null!;
            junctionTargetCol = null!;
            targetCol = null!;

            // Find junction column referencing source (via SingleLinks on junction)
            if (!junctionTable.SingleLinks.TryGetValue(sourceTable.GraphQlName, out var sourceLink))
                return false;
            // Find junction column referencing target (via SingleLinks on junction)
            if (!junctionTable.SingleLinks.TryGetValue(targetTable.GraphQlName, out var targetLink))
                return false;

            junctionSourceCol = sourceLink.ChildId;
            sourceCol = sourceLink.ParentId;
            junctionTargetCol = targetLink.ChildId;
            targetCol = targetLink.ParentId;
            return true;
        }

        private static void AddManyToManyLink(
            IDbTable sourceTable, IDbTable junctionTable, IDbTable targetTable,
            ColumnDto sourceCol, ColumnDto junctionSourceCol,
            ColumnDto junctionTargetCol, ColumnDto targetCol)
        {
            var link = new ManyToManyLink
            {
                Name = targetTable.GraphQlName,
                SourceTable = sourceTable,
                JunctionTable = junctionTable,
                TargetTable = targetTable,
                SourceColumn = sourceCol,
                JunctionSourceColumn = junctionSourceCol,
                JunctionTargetColumn = junctionTargetCol,
                TargetColumn = targetCol,
            };
            sourceTable.ManyToManyLinks[targetTable.GraphQlName] = link;
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

        private sealed class SchemaTableComparer : IEqualityComparer<(string Schema, string Name)>
        {
            public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
                string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Schema, string Name) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
        }

        private sealed class SchemaTableColumnComparer : IEqualityComparer<(string TableSchema, string TableName, string ColumnName)>
        {
            public bool Equals(
                (string TableSchema, string TableName, string ColumnName) x,
                (string TableSchema, string TableName, string ColumnName) y) =>
                string.Equals(x.TableSchema, y.TableSchema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TableName, y.TableName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ColumnName, y.ColumnName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string TableSchema, string TableName, string ColumnName) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TableSchema),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TableName),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ColumnName));
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

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
        /// Defaults to the provider-neutral <see cref="AnsiSqlTypeMapper"/>.
        /// Dialect packages (SQL Server, PostgreSQL, MySQL, SQLite) supply their own
        /// mapper (via the connection factory) to correctly map their native types.
        /// </summary>
        ITypeMapper TypeMapper => AnsiSqlTypeMapper.Instance;

        /// <summary>
        /// EAV (Entity-Attribute-Value) configurations linking meta tables to parent tables.
        /// When present, the parent table's GraphQL type gets a <c>_meta: String</c> field
        /// that returns all meta rows as a JSON object.
        /// </summary>
        IReadOnlyList<EavConfig> EavConfigs => Array.Empty<EavConfig>();

        /// <summary>
        /// Enum column mappings for lookup-table enums (Approach A). When present,
        /// allows translating between database values and GraphQL enum names for
        /// columns that reference enum tables.
        /// </summary>
        BifrostQL.Core.Schema.EnumColumnMap? EnumColumns => null;
    }

    public sealed class DbModel : IDbModel
    {
        internal static readonly Pluralizer Pluralizer = new();

        // Table lookups run on the query hot path (once per query node). A linear
        // Tables scan per call is O(tables) each; these case-insensitive indexes make
        // both lookups O(1). Built lazily because Tables is assigned via object
        // initializer after construction; first-write-wins mirrors the previous
        // FirstOrDefault semantics for any duplicate keys.
        private readonly Lazy<IReadOnlyDictionary<string, IDbTable>> _byGraphQlName;
        private readonly Lazy<IReadOnlyDictionary<string, IDbTable>> _byDbName;

        public DbModel()
        {
            _byGraphQlName = new Lazy<IReadOnlyDictionary<string, IDbTable>>(BuildGraphQlNameIndex);
            _byDbName = new Lazy<IReadOnlyDictionary<string, IDbTable>>(BuildDbNameIndex);
        }

        private IReadOnlyDictionary<string, IDbTable> BuildGraphQlNameIndex()
        {
            var dict = new Dictionary<string, IDbTable>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var t in Tables ?? Array.Empty<IDbTable>())
            {
                // MatchName accepts either the schema-qualified full name or the bare
                // GraphQL name, so both are registered. FullName mirrors
                // DbTable.FullName; kept in sync there.
                var fullName = string.Equals(t.TableSchema, "dbo", StringComparison.Ordinal)
                    ? t.GraphQlName
                    : $"{t.TableSchema}_{t.GraphQlName}";
                dict.TryAdd(fullName, t);
                dict.TryAdd(t.GraphQlName, t);
            }
            return dict;
        }

        private IReadOnlyDictionary<string, IDbTable> BuildDbNameIndex()
        {
            var dict = new Dictionary<string, IDbTable>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var t in Tables ?? Array.Empty<IDbTable>())
                dict.TryAdd(t.DbName, t);
            return dict;
        }

        public IReadOnlyCollection<IDbTable> Tables { get; init; } = null!;
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; init; } = Array.Empty<DbStoredProcedure>();
        public IDictionary<string, object?> Metadata { get; init; } = null!;
        public ITypeMapper TypeMapper { get; set; } = AnsiSqlTypeMapper.Instance;
        public IReadOnlyList<EavConfig> EavConfigs { get; set; } = Array.Empty<EavConfig>();
        public BifrostQL.Core.Schema.EnumColumnMap? EnumColumns { get; set; }
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => Utils.MetadataSwitch.Parse(GetMetadataValue(property), defaultValue);
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
            return _byGraphQlName.Value.TryGetValue(fullName, out var table)
                ? table
                : throw new ArgumentOutOfRangeException(nameof(fullName), fullName, $"failed table lookup on graphql name: {fullName}");
        }
        public IDbTable GetTableFromDbName(string tableName)
        {
            return _byDbName.Value.TryGetValue(tableName, out var table)
                ? table
                : throw new ArgumentOutOfRangeException(nameof(tableName), tableName, $"failed table lookup on db name: {tableName}");
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
            // Build a case-insensitive copy so that metadata keyed as "DBO.Users"
            // matches a table whose qualified name is "dbo.Users" (or any other
            // casing variant). Without this, misconfigured metadata is silently
            // dropped when the dictionary's default comparer is ordinal/case-sensitive.
            var ciAdditional = new Dictionary<string, IDictionary<string, object?>>(
                additionalMetadata, StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
            {
                var qualifiedName = $"{table.TableSchema}.{table.DbName}";
                if (ciAdditional.TryGetValue(qualifiedName, out var tableExtra))
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
            var prefixedTables = DeduplicateTableGraphQlNames(
                tables.Select(t => t.WithSchemaPrefix(schemaPrefixOptions)).ToList());

            var includePattern = dbMetadata.TryGetValue(MetadataKeys.StoredProcedures.Include, out var inc) ? inc?.ToString() : null;
            var excludePattern = dbMetadata.TryGetValue(MetadataKeys.StoredProcedures.Exclude, out var exc) ? exc?.ToString() : null;

            var filteredProcs = storedProcedures
                .Where(p => DbStoredProcedure.MatchesFilter(p.DbName, includePattern, excludePattern))
                .ToList();

            var model =
                new DbModel()
                {
                    Tables = prefixedTables.Where(t => t.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden) == false).ToList(),
                    StoredProcedures = filteredProcs,
                    Metadata = dbMetadata,
                };

            // Detect or parse prefix groups for name-based linking
            List<PrefixGroup> prefixGroups;
            if (dbMetadata.TryGetValue(MetadataKeys.AppSchema.PrefixGroups, out var pgValue) &&
                pgValue?.ToString() is { Length: > 0 } pgStr)
            {
                prefixGroups = ParsePrefixGroups(pgStr, model.Tables);
            }
            else
            {
                prefixGroups = DetectPrefixGroups(model.Tables);
            }

            // Run application schema detection (WordPress, Drupal, etc.), folding any
            // detected prefix groups and synthetic foreign keys into the link inputs.
            IReadOnlyCollection<DbForeignKey> allForeignKeys;
            (prefixGroups, allForeignKeys) = ApplyAppSchemaDetection(
                model, prefixedTables, dbMetadata, prefixGroups, foreignKeys);

            model.EavConfigs = CollectEavConfigs(model.Tables);

            model.LinkTables(allForeignKeys, prefixGroups);
            UnlinkHistoryTargets(model);
            return model;
        }

        /// <summary>
        /// Removes every relationship link that touches a resolved history target.
        /// History targets are system tables (epic decision D2): trail data is
        /// reachable ONLY through the generated <c>&lt;table&gt;History</c> fields,
        /// which force the entity discriminator, tenant scope, and crypto image
        /// projection — so no navigable link (schema join field, filter
        /// relationship, nested sync collection, aggregate hop) may reach a target.
        /// Stripping the links here, at the single place links are built, means
        /// schema emission, resolver wiring, and query construction all simply
        /// never see them instead of each re-implementing the exclusion. The
        /// tables themselves stay in the model: the change-history writer and the
        /// trail read field still resolve them by name.
        /// </summary>
        private static void UnlinkHistoryTargets(DbModel model)
        {
            var targets = Core.Schema.HistorySurface.ResolveTargets(model);
            if (targets.Count == 0)
                return;

            foreach (var table in model.Tables)
            {
                if (targets.Contains(table))
                {
                    table.SingleLinks.Clear();
                    table.MultiLinks.Clear();
                    table.ManyToManyLinks.Clear();
                    continue;
                }

                RemoveWhere(table.SingleLinks, link => targets.Contains(link.ParentTable) || targets.Contains(link.ChildTable));
                RemoveWhere(table.MultiLinks, link => targets.Contains(link.ParentTable) || targets.Contains(link.ChildTable));

                foreach (var key in table.ManyToManyLinks
                             .Where(kv => targets.Contains(kv.Value.SourceTable)
                                          || targets.Contains(kv.Value.JunctionTable)
                                          || targets.Contains(kv.Value.TargetTable))
                             .Select(kv => kv.Key).ToList())
                {
                    table.ManyToManyLinks.Remove(key);
                }
            }
        }

        private static void RemoveWhere(IDictionary<string, TableLinkDto> links, Func<TableLinkDto, bool> predicate)
        {
            foreach (var key in links.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList())
                links.Remove(key);
        }

        /// <summary>
        /// Runs application-schema detection (WordPress, Drupal, …) against the model
        /// and, when a schema is detected, folds its results into the relationship
        /// link inputs: merges its prefix groups, applies its additional metadata, and
        /// appends its synthetic foreign keys. Returns the (possibly augmented) prefix
        /// groups and foreign keys unchanged when nothing is detected.
        /// </summary>
        private static (List<PrefixGroup> prefixGroups, IReadOnlyCollection<DbForeignKey> foreignKeys) ApplyAppSchemaDetection(
            DbModel model,
            List<DbTable> prefixedTables,
            IDictionary<string, object?> dbMetadata,
            List<PrefixGroup> prefixGroups,
            IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            var existingSchemas = model.Tables.Select(t => t.TableSchema).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var appResult = AppSchema.AppSchemaDetectionService.Default.Detect(
                model.Tables.ToList(), dbMetadata, existingSchemas);

            if (appResult == null)
                return (prefixGroups, foreignKeys);

            var mergedGroups = MergePrefixGroups(prefixGroups, appResult);

            // Apply additional metadata from detector
            if (appResult.AdditionalMetadata.Count > 0)
                ApplyAdditionalMetadata(prefixedTables, appResult.AdditionalMetadata);

            var syntheticFks = BuildSyntheticForeignKeys(appResult, model);
            var allForeignKeys = syntheticFks.Count > 0
                ? (IReadOnlyCollection<DbForeignKey>)foreignKeys.Concat(syntheticFks).ToList()
                : foreignKeys;

            return (mergedGroups, allForeignKeys);
        }

        /// <summary>
        /// Merges detector-supplied prefix groups with the manually-configured/detected
        /// ones. Manual groups always win — they were parsed/detected first — so a
        /// detected group is appended only when its prefix is not already present.
        /// </summary>
        private static List<PrefixGroup> MergePrefixGroups(List<PrefixGroup> manual, AppSchema.AppSchemaResult appResult)
        {
            var merged = new List<PrefixGroup>(manual);
            foreach (var pg in appResult.PrefixGroups)
            {
                if (!merged.Any(g => string.Equals(g.Prefix, pg.Prefix, StringComparison.OrdinalIgnoreCase)))
                    merged.Add(pg);
            }
            return merged;
        }

        /// <summary>
        /// Converts a detector's synthetic foreign keys into <see cref="DbForeignKey"/>
        /// objects. Synthetic FKs reference tables by base name, so each end is resolved
        /// against the model's tables (case-insensitive) and the FK is dropped when
        /// either end is missing.
        /// </summary>
        private static List<DbForeignKey> BuildSyntheticForeignKeys(AppSchema.AppSchemaResult appResult, DbModel model)
        {
            var syntheticFks = new List<DbForeignKey>();
            if (appResult.ExplicitForeignKeys.Count == 0)
                return syntheticFks;

            var tableLookup = model.Tables.ToDictionary(t => t.DbName, t => t, StringComparer.OrdinalIgnoreCase);
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
            return syntheticFks;
        }

        /// <summary>
        /// Ensures every table has a unique GraphQlName. Schema prefixing is optional
        /// and disabled by default, so two tables in different schemas can still share
        /// a name (e.g. Apache AGE creates an _ag_label_edge table in every graph
        /// schema). Identical names would produce duplicate GraphQL types and crash the
        /// schema build, so collisions are disambiguated with the schema prefix here —
        /// mirroring <see cref="ColumnDto.DeduplicateGraphQlNames"/> for columns. The
        /// first occurrence keeps its bare name for backward compatibility.
        /// </summary>
        private static List<DbTable> DeduplicateTableGraphQlNames(List<DbTable> tables)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DbTable>(tables.Count);
            foreach (var table in tables)
            {
                if (taken.Add(table.GraphQlName))
                {
                    result.Add(table);
                    continue;
                }

                var baseName = $"{table.TableSchema.ToGraphQl()}_{table.GraphQlName}";
                var candidate = baseName;
                var suffix = 2;
                while (!taken.Add(candidate))
                    candidate = $"{baseName}_{suffix++}";

                result.Add(table.WithGraphQlName(candidate));
            }
            return result;
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
        /// <summary>Parent id always refers to the one in one to many relations in database joins. For composite-key FKs this is the first column; the full ordered list is on <see cref="ParentIds"/>.</summary>
        public ColumnDto ParentId { get; init; } = null!;
        /// <summary>Child id always refers to the many in one to many relations in database joins. For composite-key FKs this is the first column; the full ordered list is on <see cref="ChildIds"/>.</summary>
        public ColumnDto ChildId { get; init; } = null!;
        /// <summary>
        /// Full ordered list of parent-side columns for the link. Single-column
        /// FKs populate exactly one entry that matches <see cref="ParentId"/>.
        /// Defaults to a singleton over <see cref="ParentId"/> when callers
        /// only set the single-column properties (back-compat).
        /// </summary>
        public IReadOnlyList<ColumnDto> ParentIds
        {
            get => _parentIds ?? (ParentId is null ? Array.Empty<ColumnDto>() : new[] { ParentId });
            init => _parentIds = value;
        }
        private readonly IReadOnlyList<ColumnDto>? _parentIds;
        /// <summary>
        /// Full ordered list of child-side columns for the link. Single-column
        /// FKs populate exactly one entry that matches <see cref="ChildId"/>.
        /// </summary>
        public IReadOnlyList<ColumnDto> ChildIds
        {
            get => _childIds ?? (ChildId is null ? Array.Empty<ColumnDto>() : new[] { ChildId });
            init => _childIds = value;
        }
        private readonly IReadOnlyList<ColumnDto>? _childIds;
        /// <summary>True when the FK spans more than one column pair.</summary>
        public bool IsComposite => ChildIds.Count > 1;
        /// <summary>Optional GraphQL field override used when navigating from parent to child.</summary>
        public string? ChildFieldNameOverride { get; init; }
        /// <summary>
        /// When set, this link is polymorphic: the child join is additionally
        /// constrained by a constant equality (e.g. <c>notes.entity_type = 'company'</c>)
        /// so a single shared child table surfaces as a distinct navigable
        /// collection on each referenced parent. Null for ordinary links.
        /// </summary>
        public LinkConstantPredicate? TypePredicate { get; init; }
        /// <summary>The GraphQL field name used when navigating from child to parent.</summary>
        public string ParentFieldName => ParentTable.GraphQlName;
        /// <summary>The GraphQL field name used when navigating from parent to child.</summary>
        public string ChildFieldName => ChildFieldNameOverride
            ?? (string.Equals(ParentTable.GraphQlName, ChildTable.GraphQlName, StringComparison.OrdinalIgnoreCase)
            ? $"{ChildTable.GraphQlName}_children"
            : ChildTable.GraphQlName);

        // SQL-fragment builders for this link now live in QueryModel.TableLinkSql so
        // the Model layer stays pure data (no dialect, no SQL text). See that class
        // and its sole consumer GqlAggregateColumn.
        public override string ToString() => $"{Name}-[{ChildId.TableName}.{ChildId.ColumnName}={ParentId.TableName}.{ParentId.ColumnName}]";
    }

    /// <summary>
    /// A constant equality predicate applied to a polymorphic child join —
    /// the discriminator <paramref name="Column"/> on the child table must
    /// equal <paramref name="Value"/> (e.g. <c>entity_type = "company"</c>).
    /// </summary>
    public sealed record LinkConstantPredicate(ColumnDto Column, object Value);

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

        /// <summary>
        /// True when the junction table carries extra (non-key, non-FK) data columns
        /// beyond the two foreign keys — e.g. an enrollment date or a sort order. The
        /// link is still a many-to-many bridge; this flag lets a client reveal those
        /// payload columns instead of hiding the junction entirely.
        /// </summary>
        public bool HasPayload { get; init; }

        public override string ToString() =>
            $"M:N[{SourceTable.DbName}.{SourceColumn.ColumnName} -> {JunctionTable.DbName}({JunctionSourceColumn.ColumnName},{JunctionTargetColumn.ColumnName}) -> {TargetTable.DbName}.{TargetColumn.ColumnName}]";
    }

    public record SchemaRef(string Catalog, string Schema);
    public record TableRef(string Catalog, string Schema, string Table)
        : SchemaRef(Catalog, Schema);
    public record ColumnRef(string Catalog, string Schema, string Table, string Column)
        : TableRef(Catalog, Schema, Table);


}

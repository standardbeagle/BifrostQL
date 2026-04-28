namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Detects WordPress database schemas by looking for {prefix}users + {prefix}posts + {prefix}options
/// table patterns. Supports multisite (multiple prefixes in same database).
/// </summary>
public sealed class WordPressDetector : IAppSchemaDetector
{
    public string AppName => "wordpress";

    /// <summary>Signature tables (base names without prefix) — ALL must be present for detection.</summary>
    private static readonly string[] SignatureTables = { "users", "posts", "options" };

    /// <summary>Additional tables that strengthen confidence when present.</summary>
    private static readonly string[] SupportingTables =
    {
        "postmeta", "usermeta", "comments", "commentmeta",
        "terms", "termmeta", "term_taxonomy", "term_relationships"
    };

    /// <summary>Action scheduler tables that should be hidden by default.</summary>
    private static readonly string[] HiddenTablePatterns =
    {
        "actionscheduler_actions", "actionscheduler_claims",
        "actionscheduler_groups", "actionscheduler_logs"
    };

    /// <summary>Base table names mapped to friendly labels.</summary>
    private static readonly (string BaseName, string Label)[] TableLabels =
    {
        ("posts", "Posts"),
        ("postmeta", "Post Meta"),
        ("users", "Users"),
        ("usermeta", "User Meta"),
        ("comments", "Comments"),
        ("commentmeta", "Comment Meta"),
        ("options", "Options"),
        ("terms", "Terms"),
        ("termmeta", "Term Meta"),
        ("term_taxonomy", "Term Taxonomy"),
        ("term_relationships", "Term Relationships"),
        ("links", "Links"),
    };

    /// <summary>
    /// WordPress EAV meta tables: (MetaBaseName, ParentBaseName, FkColumn, KeyColumn, ValueColumn).
    /// </summary>
    private static readonly (string Meta, string Parent, string Fk, string Key, string Value)[] EavTables =
    {
        ("postmeta", "posts", "post_id", "meta_key", "meta_value"),
        ("usermeta", "users", "user_id", "meta_key", "meta_value"),
        ("termmeta", "terms", "term_id", "meta_key", "meta_value"),
        ("commentmeta", "comments", "comment_id", "meta_key", "meta_value"),
    };

    /// <summary>WordPress FK relationships expressed as base table names (no prefix).</summary>
    private static readonly SyntheticForeignKey[] WordPressForeignKeys =
    {
        new("posts", "post_author", "users", "ID"),
        new("posts", "post_parent", "posts", "ID"),
        new("postmeta", "post_id", "posts", "ID"),
        new("usermeta", "user_id", "users", "ID"),
        new("comments", "comment_post_ID", "posts", "ID"),
        new("comments", "user_id", "users", "ID"),
        new("commentmeta", "comment_id", "comments", "comment_ID"),
        new("termmeta", "term_id", "terms", "term_id"),
        new("term_taxonomy", "term_id", "terms", "term_id"),
        new("term_relationships", "term_taxonomy_id", "term_taxonomy", "term_taxonomy_id"),
    };

    /// <summary>
    /// Columns that typically contain serialized PHP data in WordPress.
    /// Format: (TableBaseName, ColumnName)
    /// </summary>
    private static readonly (string TableBase, string ColumnName)[] SerializedPhpColumns =
    {
        ("postmeta", "meta_value"),
        ("usermeta", "meta_value"),
        ("termmeta", "meta_value"),
        ("commentmeta", "meta_value"),
        ("options", "option_value"),
    };

    public bool IsEnabled(IDictionary<string, object?> dbMetadata)
    {
        if (dbMetadata.TryGetValue("auto-detect-app", out var val)
            && string.Equals(val?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
    {
        var tableDbNames = new HashSet<string>(tables.Select(t => t.DbName), StringComparer.Ordinal);

        // Find valid prefixes using case-sensitive matching, then merge case-variant prefixes
        // that would produce conflicting GraphQL group names
        var validPrefixes = FindValidPrefixes(tableDbNames);
        validPrefixes = MergeCaseVariantPrefixes(validPrefixes, tableDbNames);
        if (validPrefixes.Count == 0)
            return null;

        // Filter out prefixes whose group name conflicts with existing DB schemas
        var existingSchemasSet = new HashSet<string>(existingSchemas, StringComparer.OrdinalIgnoreCase);
        validPrefixes = validPrefixes
            .Where(p => !existingSchemasSet.Contains(GroupNameFromPrefix(p)))
            .ToList();

        if (validPrefixes.Count == 0)
            return null;

        var prefixGroups = new List<PrefixGroup>();
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>();
        var explicitForeignKeys = new List<SyntheticForeignKey>();
        var columnMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        // Build table schema lookup for metadata key generation
        var tableSchemaLookup = tables.ToDictionary(t => t.DbName, t => t.TableSchema, StringComparer.Ordinal);

        // Track total tables and supporting tables found for confidence calculation
        var totalTablesFound = 0;
        var supportingTablesFound = 0;

        foreach (var prefix in validPrefixes)
        {
            // Collect all tables belonging to this prefix using case-sensitive matching
            // so that wp_posts and wP_posts are correctly separated
            var groupTableNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dbName in tableDbNames)
            {
                if (dbName.StartsWith(prefix, StringComparison.Ordinal))
                    groupTableNames.Add(dbName);
            }

            totalTablesFound += groupTableNames.Count;

            // Count supporting tables for confidence calculation
            foreach (var supportingTable in SupportingTables)
            {
                if (groupTableNames.Contains(prefix + supportingTable))
                    supportingTablesFound++;
            }

            var groupName = GroupNameFromPrefix(prefix);
            prefixGroups.Add(new PrefixGroup(prefix, groupName, groupTableNames));

            // Resolve FKs: expand base names to prefixed names, only if both tables exist
            foreach (var fk in WordPressForeignKeys)
            {
                var childFull = prefix + fk.ChildTable;
                var parentFull = prefix + fk.ParentTable;
                if (groupTableNames.Contains(childFull) && groupTableNames.Contains(parentFull))
                    explicitForeignKeys.Add(new SyntheticForeignKey(childFull, fk.ChildColumn, parentFull, fk.ParentColumn));
            }

            // Inject metadata: hidden tables
            foreach (var pattern in HiddenTablePatterns)
            {
                var fullName = prefix + pattern;
                if (groupTableNames.Contains(fullName))
                {
                    var schema = tableSchemaLookup.GetValueOrDefault(fullName, "");
                    var key = string.IsNullOrEmpty(schema) ? fullName : $"{schema}.{fullName}";
                    additionalMetadata[key] = new Dictionary<string, object?> { ["visibility"] = "hidden" };
                }
            }

            // Inject metadata: labels
            foreach (var (baseName, label) in TableLabels)
            {
                var fullName = prefix + baseName;
                if (groupTableNames.Contains(fullName))
                {
                    var schema = tableSchemaLookup.GetValueOrDefault(fullName, "");
                    var key = string.IsNullOrEmpty(schema) ? fullName : $"{schema}.{fullName}";
                    if (additionalMetadata.TryGetValue(key, out var existing))
                        existing["label"] = label;
                    else
                        additionalMetadata[key] = new Dictionary<string, object?> { ["label"] = label };
                }
            }

            // Inject metadata: EAV meta table configuration
            foreach (var (meta, parent, fk, keyCol, valueCol) in EavTables)
            {
                var metaFull = prefix + meta;
                var parentFull = prefix + parent;
                if (!groupTableNames.Contains(metaFull) || !groupTableNames.Contains(parentFull))
                    continue;

                var schema = tableSchemaLookup.GetValueOrDefault(metaFull, "");
                var metadataKey = string.IsNullOrEmpty(schema) ? metaFull : $"{schema}.{metaFull}";
                if (!additionalMetadata.TryGetValue(metadataKey, out var metaDict))
                {
                    metaDict = new Dictionary<string, object?>();
                    additionalMetadata[metadataKey] = metaDict;
                }
                metaDict[MetadataKeys.Eav.Parent] = parentFull;
                metaDict[MetadataKeys.Eav.ForeignKey] = fk;
                metaDict[MetadataKeys.Eav.Key] = keyCol;
                metaDict[MetadataKeys.Eav.Value] = valueCol;
            }

            // Inject column metadata: serialized PHP columns
            foreach (var (tableBase, columnName) in SerializedPhpColumns)
            {
                var fullTableName = prefix + tableBase;
                if (!groupTableNames.Contains(fullTableName))
                    continue;

                var schema = tableSchemaLookup.GetValueOrDefault(fullTableName, "");
                // Check if the column actually exists in the table
                var table = tables.FirstOrDefault(t =>
                    string.Equals(t.DbName, fullTableName, StringComparison.Ordinal));
                if (table == null)
                    continue;

                var columnExists = table.Columns.Any(c =>
                    string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
                if (!columnExists)
                    continue;

                var columnKey = string.IsNullOrEmpty(schema)
                    ? $"{fullTableName}.{columnName}"
                    : $"{schema}.{fullTableName}.{columnName}";
                columnMetadata[columnKey] = new Dictionary<string, object?>
                {
                    ["type"] = "php_serialized",
                    ["format"] = "php"
                };
            }
        }

        var schemaResult = new AppSchemaResult(AppName, prefixGroups, additionalMetadata, explicitForeignKeys)
        {
            ColumnMetadata = columnMetadata
        };

        // Calculate confidence score based on supporting tables found
        var confidence = CalculateConfidence(supportingTablesFound, validPrefixes.Count);

        return DetectionResult.Create(AppName, confidence, schemaResult);
    }

    /// <summary>
    /// Calculate confidence score based on supporting tables found.
    /// Base confidence of 0.6 for signature tables, increases with supporting tables.
    /// </summary>
    private static double CalculateConfidence(int supportingTablesFound, int prefixCount)
    {
        // Base confidence for having all signature tables
        const double baseConfidence = 0.6;

        // Maximum additional confidence from supporting tables
        const double maxAdditionalConfidence = 0.35;

        // Normalize supporting tables by prefix count
        var normalizedSupportingTables = supportingTablesFound / (double)prefixCount;

        // Calculate additional confidence based on ratio of supporting tables found
        var supportingRatio = normalizedSupportingTables / SupportingTables.Length;
        var additionalConfidence = supportingRatio * maxAdditionalConfidence;

        // Small bonus for multiple prefixes (multisite detection)
        var multisiteBonus = prefixCount > 1 ? 0.05 : 0.0;

        return Math.Min(baseConfidence + additionalConfidence + multisiteBonus, 1.0);
    }

    /// <summary>
    /// Find all prefixes where every signature table exists with that prefix.
    /// Uses case-sensitive matching so that wp_ and wP_ are treated as distinct prefixes.
    /// </summary>
    private static List<string> FindValidPrefixes(HashSet<string> tableDbNames)
    {
        var candidatePrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in tableDbNames)
        {
            foreach (var sig in SignatureTables)
            {
                if (name.Length > sig.Length
                    && name.EndsWith(sig, StringComparison.OrdinalIgnoreCase)
                    && name[name.Length - sig.Length - 1] == '_')
                {
                    var prefix = name[..(name.Length - sig.Length)];
                    candidatePrefixes.Add(prefix);
                }
            }
        }

        // Validate: all signature tables must exist for this prefix (case-sensitive)
        return candidatePrefixes
            .Where(prefix => SignatureTables.All(
                sig => tableDbNames.Contains(prefix + sig)))
            .ToList();
    }

    /// <summary>
    /// Merge case-variant prefixes that would produce conflicting GraphQL group names.
    /// For example, wp_ and wP_ both produce group name "wp" (case-insensitive),
    /// so they are merged under the prefix that matches the most tables.
    /// </summary>
    private static List<string> MergeCaseVariantPrefixes(List<string> prefixes, HashSet<string> tableDbNames)
    {
        if (prefixes.Count <= 1)
            return prefixes;

        // Group prefixes by their case-insensitive group name
        var groups = prefixes.GroupBy(p => GroupNameFromPrefix(p), StringComparer.OrdinalIgnoreCase);

        var merged = new List<string>();
        foreach (var group in groups)
        {
            var variants = group.ToList();
            if (variants.Count == 1)
            {
                merged.Add(variants[0]);
                continue;
            }

            // Pick the prefix with the most matching tables
            var best = variants
                .OrderByDescending(prefix => tableDbNames.Count(t => t.StartsWith(prefix, StringComparison.Ordinal)))
                .First();
            merged.Add(best);
        }

        return merged;
    }

    private static string GroupNameFromPrefix(string prefix) =>
        prefix.EndsWith('_') ? prefix[..^1] : prefix;
}

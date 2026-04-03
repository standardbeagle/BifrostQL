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

    public bool IsEnabled(IDictionary<string, object?> dbMetadata)
    {
        if (dbMetadata.TryGetValue("auto-detect-app", out var val)
            && string.Equals(val?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public AppSchemaResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
    {
        var tableDbNames = new HashSet<string>(tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);

        // Find valid prefixes: for each candidate prefix, all signature tables must exist
        var validPrefixes = FindValidPrefixes(tableDbNames);
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

        // Build table schema lookup for metadata key generation
        var tableSchemaLookup = tables.ToDictionary(t => t.DbName, t => t.TableSchema, StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in validPrefixes)
        {
            var groupName = GroupNameFromPrefix(prefix);

            // Collect all tables belonging to this prefix
            var groupTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dbName in tableDbNames)
            {
                if (dbName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    groupTableNames.Add(dbName);
            }

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
                metaDict["eav-parent"] = parentFull;
                metaDict["eav-fk"] = fk;
                metaDict["eav-key"] = keyCol;
                metaDict["eav-value"] = valueCol;
            }
        }

        return new AppSchemaResult(AppName, prefixGroups, additionalMetadata, explicitForeignKeys);
    }

    /// <summary>
    /// Find all prefixes where every signature table exists with that prefix.
    /// For each table name, try to extract a prefix by checking if it ends with a signature table base name.
    /// </summary>
    private static List<string> FindValidPrefixes(HashSet<string> tableDbNames)
    {
        var candidatePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in tableDbNames)
        {
            foreach (var sig in SignatureTables)
            {
                if (name.Length > sig.Length
                    && name.EndsWith(sig, StringComparison.OrdinalIgnoreCase)
                    && name[name.Length - sig.Length - 1] == '_')
                {
                    // prefix includes the trailing underscore before the signature base name
                    var prefix = name[..(name.Length - sig.Length)];
                    candidatePrefixes.Add(prefix);
                }
            }
        }

        // Validate: all signature tables must exist for this prefix
        return candidatePrefixes
            .Where(prefix => SignatureTables.All(
                sig => tableDbNames.Contains(prefix + sig)))
            .ToList();
    }

    private static string GroupNameFromPrefix(string prefix) =>
        prefix.EndsWith('_') ? prefix[..^1] : prefix;
}

namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Detects Drupal database schemas by looking for signature tables
/// like node, node_field_data, users_field_data, etc.
/// </summary>
public sealed class DrupalDetector : IAppSchemaDetector
{
    public string AppName => "drupal";

    /// <summary>Signature tables that indicate a Drupal installation.</summary>
    private static readonly string[] SignatureTables =
    {
        "node",
        "node_field_data",
        "users_field_data"
    };

    /// <summary>Supporting tables that strengthen confidence when present.</summary>
    private static readonly string[] SupportingTables =
    {
        "node_access",
        "node_revision",
        "node_field_revision",
        "taxonomy_term_field_data",
        "taxonomy_term_hierarchy",
        "taxonomy_index",
        "comment_field_data",
        "comment_entity_statistics",
        "users",
        "sessions",
        "key_value",
        "config",
        "cache_bootstrap",
        "cache_config",
        "cache_data",
        "cache_default",
        "cache_discovery",
        "cache_entity",
        "cache_menu",
        "cache_page",
        "cache_render",
        "cache_toolbar",
        "watchdog",
        "semaphore",
        "queue"
    };

    /// <summary>Cache tables that should be hidden by default.</summary>
    private static readonly string[] HiddenTablePatterns =
    {
        "cache_",
        "semaphore",
        "queue",
        "sessions",
        "watchdog"
    };

    /// <summary>Base table names mapped to friendly labels.</summary>
    private static readonly (string BaseName, string Label)[] TableLabels =
    {
        ("node", "Nodes"),
        ("node_field_data", "Node Data"),
        ("node_access", "Node Access"),
        ("node_revision", "Node Revisions"),
        ("node_field_revision", "Node Field Revisions"),
        ("users", "Users"),
        ("users_field_data", "User Data"),
        ("taxonomy_term_field_data", "Taxonomy Terms"),
        ("taxonomy_term_hierarchy", "Term Hierarchy"),
        ("taxonomy_index", "Taxonomy Index"),
        ("comment_field_data", "Comments"),
        ("comment_entity_statistics", "Comment Statistics"),
    };

    /// <summary>Drupal FK relationships expressed as base table names.</summary>
    private static readonly SyntheticForeignKey[] DrupalForeignKeys =
    {
        new("node_field_data", "uid", "users_field_data", "uid"),
        new("node_field_data", "vid", "node_revision", "vid"),
        new("node_revision", "nid", "node", "nid"),
        new("node_field_revision", "nid", "node", "nid"),
        new("node_field_revision", "vid", "node_revision", "vid"),
        new("taxonomy_term_field_data", "vid", "taxonomy_vocabulary", "vid"),
        new("taxonomy_term_hierarchy", "tid", "taxonomy_term_field_data", "tid"),
        new("taxonomy_index", "nid", "node_field_data", "nid"),
        new("taxonomy_index", "tid", "taxonomy_term_field_data", "tid"),
        new("comment_field_data", "entity_id", "node_field_data", "nid"),
        new("comment_field_data", "uid", "users_field_data", "uid"),
        new("comment_entity_statistics", "entity_id", "node_field_data", "nid"),
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
        var tableDbNames = new HashSet<string>(tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);

        // Check for signature tables - all must be present
        var hasAllSignatures = SignatureTables.All(sig => tableDbNames.Contains(sig));
        if (!hasAllSignatures)
            return null;

        // Check for conflicting schema name
        var existingSchemasSet = new HashSet<string>(existingSchemas, StringComparer.OrdinalIgnoreCase);
        if (existingSchemasSet.Contains("drupal"))
            return null;

        // Collect supporting tables for confidence calculation
        var supportingTablesFound = SupportingTables.Count(t => tableDbNames.Contains(t));

        // Build prefix group (Drupal doesn't use prefixes, so use empty prefix)
        var prefixGroups = new List<PrefixGroup>
        {
            new PrefixGroup("", "drupal", tableDbNames)
        };

        // Build metadata and foreign keys
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>();
        var explicitForeignKeys = new List<SyntheticForeignKey>();

        // Build table schema lookup for metadata key generation
        var tableSchemaLookup = tables.ToDictionary(t => t.DbName, t => t.TableSchema, StringComparer.OrdinalIgnoreCase);

        // Inject metadata: hidden tables
        foreach (var dbName in tableDbNames)
        {
            foreach (var pattern in HiddenTablePatterns)
            {
                if (dbName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var schema = tableSchemaLookup.GetValueOrDefault(dbName, "");
                    var key = string.IsNullOrEmpty(schema) ? dbName : $"{schema}.{dbName}";
                    additionalMetadata[key] = new Dictionary<string, object?> { ["visibility"] = "hidden" };
                    break;
                }
            }
        }

        // Inject metadata: labels
        foreach (var (baseName, label) in TableLabels)
        {
            if (tableDbNames.Contains(baseName))
            {
                var schema = tableSchemaLookup.GetValueOrDefault(baseName, "");
                var key = string.IsNullOrEmpty(schema) ? baseName : $"{schema}.{baseName}";
                if (additionalMetadata.TryGetValue(key, out var existing))
                    existing["label"] = label;
                else
                    additionalMetadata[key] = new Dictionary<string, object?> { ["label"] = label };
            }
        }

        // Add explicit foreign keys
        foreach (var fk in DrupalForeignKeys)
        {
            if (tableDbNames.Contains(fk.ChildTable) && tableDbNames.Contains(fk.ParentTable))
                explicitForeignKeys.Add(fk);
        }

        var schemaResult = new AppSchemaResult(AppName, prefixGroups, additionalMetadata, explicitForeignKeys);

        // Calculate confidence based on supporting tables
        var confidence = CalculateConfidence(supportingTablesFound);

        return DetectionResult.Create(AppName, confidence, schemaResult);
    }

    /// <summary>
    /// Calculate confidence score based on supporting tables found.
    /// Base confidence of 0.65 for signature tables, increases with supporting tables.
    /// </summary>
    private static double CalculateConfidence(int supportingTablesFound)
    {
        // Base confidence for having all signature tables
        const double baseConfidence = 0.65;

        // Maximum additional confidence from supporting tables
        const double maxAdditionalConfidence = 0.3;

        // Calculate additional confidence based on ratio of supporting tables found
        var supportingRatio = supportingTablesFound / (double)SupportingTables.Length;
        var additionalConfidence = supportingRatio * maxAdditionalConfidence;

        return Math.Min(baseConfidence + additionalConfidence, 1.0);
    }
}

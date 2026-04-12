using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// A comprehensive bundle that packages all WordPress-specific configurations,
/// detectors, and transformations for BifrostQL.
/// 
/// This bundle provides:
/// - Automatic WordPress schema detection
/// - PHP serialized data handling
/// - EAV flattener for meta tables
/// - File storage for attachments
/// - Pre-configured foreign key relationships
/// - Hidden tables (Action Scheduler, etc.)
/// </summary>
public sealed class WordPressSchemaBundle
{
    private readonly WordPressBundleConfiguration _config;
    private readonly WordPressDetector _detector;

    /// <summary>
    /// Creates a new WordPress schema bundle with the specified configuration.
    /// </summary>
    public WordPressSchemaBundle(WordPressBundleConfiguration? config = null)
    {
        _config = config ?? WordPressBundleConfiguration.Default;
        _detector = new WordPressDetector();
    }

    /// <summary>
    /// The bundle configuration.
    /// </summary>
    public WordPressBundleConfiguration Configuration => _config;

    /// <summary>
    /// The WordPress detector used for auto-detection.
    /// </summary>
    public IAppSchemaDetector Detector => _detector;

    /// <summary>
    /// Detects WordPress schema in the provided tables and returns the result.
    /// </summary>
    public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string>? existingSchemas = null)
    {
        if (!_config.EnableAutoDetection)
            return null;

        return _detector.Detect(tables, existingSchemas ?? Array.Empty<string>());
    }

    /// <summary>
    /// Checks if the database metadata indicates WordPress support should be enabled.
    /// </summary>
    public bool IsEnabled(IDictionary<string, object?> dbMetadata)
    {
        if (!_config.EnableAutoDetection)
            return false;

        return _detector.IsEnabled(dbMetadata);
    }

    /// <summary>
    /// Builds metadata annotations for WordPress tables based on configuration.
    /// </summary>
    public IReadOnlyList<string> BuildMetadataAnnotations(DetectionResult detectionResult)
    {
        var annotations = new List<string>();
        var result = detectionResult.SchemaResult;

        // Add table metadata annotations
        foreach (var (tableKey, metadata) in result.AdditionalMetadata)
        {
            var metaParts = new List<string>();
            foreach (var (key, value) in metadata)
            {
                if (value != null)
                {
                    metaParts.Add($"{key}: {value}");
                }
            }
            if (metaParts.Count > 0)
            {
                annotations.Add($"{tableKey} {{ {string.Join("; ", metaParts)} }}");
            }
        }

        // Add column metadata annotations for PHP serialized columns
        foreach (var (columnKey, metadata) in result.ColumnMetadata)
        {
            var metaParts = new List<string>();
            foreach (var (key, value) in metadata)
            {
                if (value != null)
                {
                    metaParts.Add($"{key}: {value}");
                }
            }
            if (metaParts.Count > 0)
            {
                annotations.Add($"{columnKey} {{ {string.Join("; ", metaParts)} }}");
            }
        }

        return annotations;
    }

    /// <summary>
    /// Creates an EAV module configuration for the detected WordPress schema.
    /// </summary>
    public IReadOnlyList<EavConfig> BuildEavConfigs(DetectionResult detectionResult)
    {
        if (!_config.EnableEavFlattening)
            return Array.Empty<EavConfig>();

        var configs = new List<EavConfig>();
        var result = detectionResult.SchemaResult;

        // Extract EAV configurations from metadata
        foreach (var (tableKey, metadata) in result.AdditionalMetadata)
        {
            if (metadata.TryGetValue(MetadataKeys.Eav.Parent, out var parentValue) &&
                metadata.TryGetValue(MetadataKeys.Eav.ForeignKey, out var fkValue) &&
                metadata.TryGetValue(MetadataKeys.Eav.Key, out var keyValue) &&
                metadata.TryGetValue(MetadataKeys.Eav.Value, out var valueValue))
            {
                var metaTableName = tableKey.Contains('.') ? tableKey.Split('.').Last() : tableKey;
                var parentTableName = parentValue?.ToString();
                var fkColumn = fkValue?.ToString();
                var keyColumn = keyValue?.ToString();
                var valueColumn = valueValue?.ToString();

                if (!string.IsNullOrEmpty(parentTableName) &&
                    !string.IsNullOrEmpty(fkColumn) &&
                    !string.IsNullOrEmpty(keyColumn) &&
                    !string.IsNullOrEmpty(valueColumn))
                {
                    configs.Add(new EavConfig(
                        metaTableName,
                        parentTableName,
                        fkColumn,
                        keyColumn,
                        valueColumn));
                }
            }
        }

        return configs;
    }

    /// <summary>
    /// Creates file storage configuration for WordPress attachments.
    /// </summary>
    public FileStorageConfiguration? BuildFileStorageConfig(DetectionResult detectionResult)
    {
        if (!_config.EnableFileStorage || _config.FileStorageConfig == null)
            return null;

        return new FileStorageConfiguration
        {
            BucketConfig = _config.FileStorageConfig,
            // WordPress attachments are typically in the wp_posts table with post_type = 'attachment'
            AttachmentTablePattern = "_posts",
            AttachmentTypeColumn = "post_type",
            AttachmentTypeValue = "attachment",
            GuidColumn = "guid",
        };
    }

    /// <summary>
    /// Gets the column metadata for PHP serialized columns.
    /// </summary>
    public IDictionary<string, IDictionary<string, object?>> GetSerializedColumnMetadata(DetectionResult detectionResult)
    {
        if (!_config.EnablePhpSerialization)
            return new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        return detectionResult.SchemaResult.ColumnMetadata;
    }

    /// <summary>
    /// Gets all table prefixes detected in the WordPress schema.
    /// </summary>
    public IReadOnlyList<string> GetDetectedPrefixes(DetectionResult detectionResult)
    {
        return detectionResult.SchemaResult.PrefixGroups.Select(g => g.Prefix).ToList();
    }

    /// <summary>
    /// Gets the foreign key relationships for the detected WordPress schema.
    /// </summary>
    public IReadOnlyList<SyntheticForeignKey> GetForeignKeys(DetectionResult detectionResult)
    {
        if (!_config.InjectForeignKeys)
            return Array.Empty<SyntheticForeignKey>();

        return detectionResult.SchemaResult.ExplicitForeignKeys;
    }
}

/// <summary>
/// Configuration for file storage integration with WordPress.
/// </summary>
public sealed class FileStorageConfiguration
{
    /// <summary>
    /// The storage bucket configuration.
    /// </summary>
    public required StorageBucketConfig BucketConfig { get; set; }

    /// <summary>
    /// Pattern to match attachment tables (suffix match).
    /// </summary>
    public string? AttachmentTablePattern { get; set; }

    /// <summary>
    /// Column name that identifies attachment type.
    /// </summary>
    public string? AttachmentTypeColumn { get; set; }

    /// <summary>
    /// Value in the attachment type column that indicates an attachment.
    /// </summary>
    public string? AttachmentTypeValue { get; set; }

    /// <summary>
    /// Column containing the file URL or GUID.
    /// </summary>
    public string? GuidColumn { get; set; }
}

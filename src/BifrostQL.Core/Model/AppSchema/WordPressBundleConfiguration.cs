namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Configuration options for the WordPress schema bundle.
/// Controls which WordPress-specific features are enabled.
/// </summary>
public sealed class WordPressBundleConfiguration
{
    /// <summary>
    /// Whether to enable automatic WordPress schema detection.
    /// Default: true
    /// </summary>
    public bool EnableAutoDetection { get; set; } = true;

    /// <summary>
    /// Whether to enable PHP serialized data handling for meta_value and option_value columns.
    /// Default: true
    /// </summary>
    public bool EnablePhpSerialization { get; set; } = true;

    /// <summary>
    /// Whether to enable EAV flattening for meta tables (postmeta, usermeta, etc.).
    /// Default: true
    /// </summary>
    public bool EnableEavFlattening { get; set; } = true;

    /// <summary>
    /// Whether to enable file storage support for WordPress attachments.
    /// Default: false (must be explicitly enabled with storage configuration)
    /// </summary>
    public bool EnableFileStorage { get; set; } = false;

    /// <summary>
    /// Whether to hide Action Scheduler tables by default.
    /// Default: true
    /// </summary>
    public bool HideActionSchedulerTables { get; set; } = true;

    /// <summary>
    /// Whether to inject friendly labels for WordPress tables.
    /// Default: true
    /// </summary>
    public bool InjectTableLabels { get; set; } = true;

    /// <summary>
    /// Whether to inject explicit foreign key relationships.
    /// Default: true
    /// </summary>
    public bool InjectForeignKeys { get; set; } = true;

    /// <summary>
    /// Custom table prefix to use instead of auto-detection.
    /// When null, auto-detection is used.
    /// </summary>
    public string? CustomPrefix { get; set; }

    /// <summary>
    /// Storage bucket configuration for file uploads.
    /// Required when EnableFileStorage is true.
    /// </summary>
    public Storage.StorageBucketConfig? FileStorageConfig { get; set; }

    /// <summary>
    /// Additional metadata to inject for WordPress tables.
    /// Merged with bundle-generated metadata.
    /// </summary>
    public IDictionary<string, IDictionary<string, object?>>? AdditionalMetadata { get; set; }

    /// <summary>
    /// Creates a default configuration with all features enabled.
    /// </summary>
    public static WordPressBundleConfiguration Default => new();

    /// <summary>
    /// Creates a minimal configuration with only detection and FKs enabled.
    /// </summary>
    public static WordPressBundleConfiguration Minimal => new()
    {
        EnablePhpSerialization = false,
        EnableEavFlattening = false,
        EnableFileStorage = false,
        HideActionSchedulerTables = true,
        InjectTableLabels = false,
    };

    /// <summary>
    /// Creates a configuration with all features enabled including file storage.
    /// </summary>
    public static WordPressBundleConfiguration FullFeatured(Storage.StorageBucketConfig storageConfig) => new()
    {
        EnableFileStorage = true,
        FileStorageConfig = storageConfig,
    };
}

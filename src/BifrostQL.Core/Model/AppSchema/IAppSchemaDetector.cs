namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Interface for detecting known application schemas (WordPress, Drupal, etc.)
/// and providing pre-built configurations.
/// </summary>
public interface IAppSchemaDetector
{
    /// <summary>Application name (e.g., "wordpress", "drupal")</summary>
    string AppName { get; }

    /// <summary>Check if this detector is enabled based on database metadata.</summary>
    bool IsEnabled(IDictionary<string, object?> dbMetadata);

    /// <summary>
    /// Detect the application schema from the table list.
    /// Returns a DetectionResult with confidence score, or null if not detected.
    /// </summary>
    DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas);
}

/// <summary>
/// Result of application schema detection including confidence score.
/// </summary>
public sealed class DetectionResult
{
    /// <summary>Application name (e.g., "wordpress", "drupal")</summary>
    public required string AppName { get; init; }

    /// <summary>
    /// Confidence score from 0.0 to 1.0.
    /// 0.0 = no confidence, 1.0 = absolute certainty.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>The detected schema result with prefix groups, metadata, and foreign keys.</summary>
    public required AppSchemaResult SchemaResult { get; init; }

    /// <summary>
    /// Creates a DetectionResult with validation.
    /// Confidence is clamped to [0.0, 1.0] range.
    /// </summary>
    public static DetectionResult Create(string appName, double confidence, AppSchemaResult schemaResult)
    {
        ArgumentNullException.ThrowIfNull(appName);
        ArgumentNullException.ThrowIfNull(schemaResult);

        // Clamp confidence to valid range
        var clampedConfidence = Math.Clamp(confidence, 0.0, 1.0);

        return new DetectionResult
        {
            AppName = appName,
            Confidence = clampedConfidence,
            SchemaResult = schemaResult
        };
    }
}

/// <summary>
/// Column-level metadata for a specific table column.
/// Key format: "schema.table.column" or "table.column"
/// </summary>
public record ColumnMetadataEntry(string ColumnKey, IDictionary<string, object?> Metadata);

/// <summary>
/// Result of application schema detection.
/// </summary>
public record AppSchemaResult(
    string AppName,
    IReadOnlyList<PrefixGroup> PrefixGroups,
    IDictionary<string, IDictionary<string, object?>> AdditionalMetadata,
    IReadOnlyList<SyntheticForeignKey> ExplicitForeignKeys)
{
    /// <summary>
    /// Column-level metadata keyed by "schema.table.column" or "table.column".
    /// Applied to columns after table metadata is processed.
    /// </summary>
    public IDictionary<string, IDictionary<string, object?>> ColumnMetadata { get; init; } =
        new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A foreign key relationship injected by app detection (not present in database).
/// Uses base table names (without prefix) - the detection service resolves to full names.
/// </summary>
public record SyntheticForeignKey(
    string ChildTable, string ChildColumn,
    string ParentTable, string ParentColumn);

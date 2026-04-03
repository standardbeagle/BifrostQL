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

    /// <summary>Detect the application schema from the table list. Returns null if not detected.</summary>
    AppSchemaResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas);
}

/// <summary>
/// Result of application schema detection.
/// </summary>
public record AppSchemaResult(
    string AppName,
    IReadOnlyList<PrefixGroup> PrefixGroups,
    IDictionary<string, IDictionary<string, object?>> AdditionalMetadata,
    IReadOnlyList<SyntheticForeignKey> ExplicitForeignKeys);

/// <summary>
/// A foreign key relationship injected by app detection (not present in database).
/// Uses base table names (without prefix) - the detection service resolves to full names.
/// </summary>
public record SyntheticForeignKey(
    string ChildTable, string ChildColumn,
    string ParentTable, string ParentColumn);

namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Orchestrates application schema detection. Runs registered detectors
/// and applies the first matching result.
/// </summary>
public sealed class AppSchemaDetectionService
{
    private readonly IReadOnlyList<IAppSchemaDetector> _detectors;

    public AppSchemaDetectionService(IEnumerable<IAppSchemaDetector> detectors)
    {
        _detectors = detectors.ToList();
    }

    /// <summary>Default instance with all built-in detectors.</summary>
    public static AppSchemaDetectionService Default { get; } = new(new IAppSchemaDetector[]
    {
        new WordPressDetector(),
    });

    /// <summary>
    /// Run detection against tables using database metadata for configuration.
    /// Returns null if no app schema is detected.
    /// </summary>
    public AppSchemaResult? Detect(
        IReadOnlyList<IDbTable> tables,
        IDictionary<string, object?> dbMetadata,
        IReadOnlyCollection<string>? existingSchemas = null)
    {
        var schemas = existingSchemas ?? Array.Empty<string>();

        // If auto-detect is explicitly disabled, bail out
        if (dbMetadata.TryGetValue("auto-detect-app", out var autoDetect) &&
            string.Equals(autoDetect?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // If a specific app schema is forced, run only that detector
        if (dbMetadata.TryGetValue("app-schema", out var appSchema) &&
            appSchema?.ToString() is { Length: > 0 } forced)
        {
            var specific = _detectors.FirstOrDefault(
                d => string.Equals(d.AppName, forced, StringComparison.OrdinalIgnoreCase));
            if (specific == null)
                return null;

            var result = specific.Detect(tables, schemas);
            if (result != null)
                dbMetadata["detected-app"] = result.AppName;
            return result;
        }

        // Run all enabled detectors; first match wins
        foreach (var detector in _detectors)
        {
            if (!detector.IsEnabled(dbMetadata))
                continue;

            var result = detector.Detect(tables, schemas);
            if (result != null)
            {
                dbMetadata["detected-app"] = result.AppName;
                return result;
            }
        }

        return null;
    }
}

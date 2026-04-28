namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Orchestrates application schema detection. Runs registered detectors
/// and selects the result with the highest confidence score.
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
        new DrupalDetector(),
    });

    /// <summary>
    /// Minimum confidence threshold for a detection result to be considered valid.
    /// Results below this threshold are discarded.
    /// </summary>
    public double MinimumConfidenceThreshold { get; init; } = 0.5;

    /// <summary>
    /// Run detection against tables using database metadata for configuration.
    /// Returns null if no app schema is detected with sufficient confidence.
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

            var forcedResult = specific.Detect(tables, schemas);
            if (forcedResult != null && forcedResult.Confidence >= MinimumConfidenceThreshold)
            {
                dbMetadata["detected-app"] = forcedResult.AppName;
                dbMetadata["detection-confidence"] = forcedResult.Confidence;
                return forcedResult.SchemaResult;
            }
            return null;
        }

        // Run all enabled detectors and collect results with confidence scores
        var results = new List<DetectionResult>();
        foreach (var detector in _detectors)
        {
            if (!detector.IsEnabled(dbMetadata))
                continue;

            var result = detector.Detect(tables, schemas);
            if (result != null && result.Confidence >= MinimumConfidenceThreshold)
            {
                results.Add(result);
            }
        }

        // Select the result with the highest confidence
        var bestResult = results.OrderByDescending(r => r.Confidence).FirstOrDefault();
        if (bestResult != null)
        {
            dbMetadata["detected-app"] = bestResult.AppName;
            dbMetadata["detection-confidence"] = bestResult.Confidence;
            return bestResult.SchemaResult;
        }

        return null;
    }

    /// <summary>
    /// Run detection and return detailed results for all detectors.
    /// Useful for debugging and diagnostics.
    /// </summary>
    public IReadOnlyList<DetectionResult> DetectAll(
        IReadOnlyList<IDbTable> tables,
        IDictionary<string, object?> dbMetadata,
        IReadOnlyCollection<string>? existingSchemas = null)
    {
        var schemas = existingSchemas ?? Array.Empty<string>();
        var results = new List<DetectionResult>();

        // If auto-detect is explicitly disabled, return empty
        if (dbMetadata.TryGetValue("auto-detect-app", out var autoDetect) &&
            string.Equals(autoDetect?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return results;
        }

        foreach (var detector in _detectors)
        {
            if (!detector.IsEnabled(dbMetadata))
                continue;

            var result = detector.Detect(tables, schemas);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results.OrderByDescending(r => r.Confidence).ToList();
    }
}
